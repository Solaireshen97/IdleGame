using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class FormationAdmissionTests
{
    [Fact]
    public async Task ConcurrentAdmissionAndIdleApplyCannotProduceMixedConfiguration()
    {
        await using var test = await Fixture.CreateAsync();
        var admissionFormation = await test.FormationAsync(1);
        var idleFormation = await test.SecondFormationAsync(1);
        await using var admissionDb = test.NewDb();
        await using var applyDb = test.NewDb();
        var admissionActor = (await admissionDb.Characters.FindAsync(1))!;
        var applyActor = (await applyDb.Characters.FindAsync(1))!;
        Assert.Equal(admissionActor.Version, applyActor.Version);
        using var gate = new Barrier(2);
        var admissionTask = Task.Run(async () =>
        {
            gate.SignalAndWait();
            return await test.MakeRooms(admissionDb).CreateRoomAsync(test.DungeonId, null, "token-1", characterId: 1,
                loadoutSelection: Select(admissionFormation), requestId: "racing-admission");
        });
        var applyTask = Task.Run(async () =>
        {
            gate.SignalAndWait();
            return await test.MakeFormations(applyDb).ApplyAsync("token-1", 1, idleFormation.Id,
                new ApplyFormationRequest { ExpectedVersion = idleFormation.Version,
                    ExpectedCharacterVersion = applyActor.Version, RequestId = "racing-apply" });
        });
        await Task.WhenAll(admissionTask, applyTask);
        var admission = await admissionTask;
        var apply = await applyTask;
        Assert.Equal(1, (admission.Error is null ? 1 : 0) + (apply.Error is null ? 1 : 0));
        if (admission.Error is not null) Assert.Equal("ConcurrencyConflict", admission.Error);
        if (apply.Error is not null) Assert.Contains(apply.Error, new[] { "ConcurrencyConflict", "CharacterBusy" });
        await using var restored = test.NewDb();
        var actual = await test.MakeLoadouts(restored).CaptureAsync(1);
        var winner = admission.Error is null ? admissionFormation : idleFormation;
        Assert.Equal(CombatLoadoutCodec.ConfigurationHash(winner.Loadout), CombatLoadoutCodec.ConfigurationHash(actual));
        Assert.Equal(admission.Error is null ? 1 : 0, await restored.Rooms.CountAsync());
        if (admission.Error is null)
        {
            var room = await restored.Rooms.SingleAsync();
            var slot = await restored.RoomSlots.SingleAsync(s => s.CharacterId == 1);
            var character = (await restored.Characters.FindAsync(1))!;
            Assert.Null(await new BattleLoadoutIntegrityService(restored, test.MakeLoadouts(restored))
                .EnsureAsync(room, [new(slot, character)]));
            Assert.Equal(CombatLoadoutCodec.ConfigurationHash(actual),
                CombatLoadoutCodec.ConfigurationHash(CombatLoadoutCodec.Deserialize(slot.AppliedLoadoutJson!)));
        }
    }

    [Fact]
    public async Task ConcurrentSameRequestCreatesOneRoomAndRetryRecoversReceipt()
    {
        await using var test = await Fixture.CreateAsync();
        var formation = await test.FormationAsync(1);
        await using var firstDb = test.NewDb();
        await using var secondDb = test.NewDb();
        await firstDb.Characters.FindAsync(1);
        await secondDb.Characters.FindAsync(1);
        using var gate = new Barrier(2);
        async Task<(RoomDetailResponse? Detail, string? Error)> Submit(GameDbContext db)
        {
            gate.SignalAndWait();
            return await test.MakeRooms(db).CreateRoomAsync(test.DungeonId, null, "token-1", characterId: 1,
                loadoutSelection: Select(formation), requestId: "same-concurrent-request");
        }
        var results = await Task.WhenAll(Task.Run(() => Submit(firstDb)), Task.Run(() => Submit(secondDb)));
        Assert.Contains(results, result => result.Error is null);
        Assert.All(results, result => Assert.True(result.Error is null or "ConcurrencyConflict", result.Error));
        await using var restored = test.NewDb();
        Assert.Single(await restored.Rooms.ToListAsync());
        Assert.Single(await restored.BattleAdmissionReceipts.ToListAsync());
        Assert.Single(await restored.CharacterActivities.ToListAsync());
        Assert.Null((await restored.Rooms.SingleAsync()).LoadoutIntegrityError);
        var restoredSlot = await restored.RoomSlots.SingleAsync(s => s.CharacterId == 1);
        Assert.Equal(CombatLoadoutCodec.ConfigurationHash(await test.MakeLoadouts(restored).CaptureAsync(1)),
            CombatLoadoutCodec.ConfigurationHash(CombatLoadoutCodec.Deserialize(restoredSlot.AppliedLoadoutJson!)));
        var preference = await restored.CharacterBattleFormationPreferences.SingleAsync();
        Assert.Equal(1, preference.Version);
        var (retry, error) = await test.MakeRooms(restored).CreateRoomAsync(test.DungeonId, null, "token-1", characterId: 1,
            loadoutSelection: Select(formation), requestId: "same-concurrent-request");
        Assert.Null(error);
        Assert.Equal((await restored.Rooms.SingleAsync()).Id, retry!.RoomId);
        Assert.Equal(1, (await restored.CharacterBattleFormationPreferences.SingleAsync()).Version);
    }

    [Fact]
    public async Task SuccessfulAdmissionsRememberEachCharacterAndDepthIndependently()
    {
        await using var test = await Fixture.CreateAsync();
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var depths = new DungeonDepthCatalog(Options.Create(configuration.GetSection(DungeonDepthOptions.SectionName)
            .Get<DungeonDepthOptions>()!));
        var codes = depths.DungeonCodes.ToList();
        var dungeon = await test.Db.Dungeons.Where(d => d.IsVisible && d.DungeonKind == "Dungeon" && codes.Contains(d.Code))
            .OrderBy(d => d.SortOrder).FirstAsync();
        test.Db.UserDungeonClears.Add(new UserDungeonClear { UserId = 1, DungeonId = dungeon.Id,
            HighestDepth = 4, ClearedAtUtc = DateTime.UtcNow });
        await test.Db.SaveChangesAsync();
        var levelOne = await test.FormationAsync(1);
        var levelFour = await test.SecondFormationAsync(1);
        var other = await test.AddCharacterAsync(1, "隔离记忆角色");
        var otherFormation = await test.FormationAsync(other.Id);
        var rooms = test.MakeRooms(test.Db, depths);
        var first = await rooms.CreateRoomAsync(dungeon.Id, null, "token-1", depthLevel: 1, characterId: 1,
            loadoutSelection: Select(levelOne));
        Assert.Null(first.Error);
        Assert.Null((await rooms.RemoveSlotAsync(first.Detail!.RoomId, 1, "token-1")).Error);
        var deep = await rooms.CreateRoomAsync(dungeon.Id, null, "token-1", depthLevel: 4, characterId: 1,
            loadoutSelection: Select(levelFour));
        Assert.Null(deep.Error);
        var additional = await rooms.CreateRoomAsync(dungeon.Id, null, "token-1", depthLevel: 1, characterId: other.Id,
            loadoutSelection: Select(otherFormation));
        Assert.Null(additional.Error);
        await using var restored = test.NewDb();
        var memories = await restored.CharacterBattleFormationPreferences.Where(p => p.DungeonCode == dungeon.Code).ToListAsync();
        Assert.Equal(3, memories.Count);
        Assert.Equal(levelOne.Id, memories.Single(p => p.CharacterId == 1 && p.DepthLevel == 1).FormationId);
        Assert.Equal(levelFour.Id, memories.Single(p => p.CharacterId == 1 && p.DepthLevel == 4).FormationId);
        Assert.Equal(otherFormation.Id, memories.Single(p => p.CharacterId == other.Id && p.DepthLevel == 1).FormationId);
        Assert.Equal(levelOne.Id, (await test.MakeFormations(restored).RecommendAsync("token-1", 1, dungeon.Code, 1)).Response!.Selection.FormationId);
        Assert.Equal(levelFour.Id, (await test.MakeFormations(restored).RecommendAsync("token-1", 1, dungeon.Code, 4)).Response!.Selection.FormationId);
        Assert.Equal(otherFormation.Id, (await test.MakeFormations(restored).RecommendAsync("token-1", other.Id, dungeon.Code, 1)).Response!.Selection.FormationId);
    }

    [Fact]
    public async Task CreateAppliesAllChoicesRemembersEncounterAndKeepsProfessionProgress()
    {
        await using var test = await Fixture.CreateAsync();
        var formation = await test.FormationAsync(1);
        var (detail, error) = await test.CreateRoomAsync(1, formation);
        Assert.Null(error);
        Assert.NotNull(detail);
        await test.AssertAppliedAsync(1, formation, detail.RoomId);
        Assert.Equal((5, 9), ((await test.Db.CharacterCombatProfessions.FindAsync(1, "knight"))!.Level,
            (await test.Db.CharacterCombatProfessions.FindAsync(1, "knight"))!.Experience));
        var current = await test.Db.Characters.FindAsync(1);
        Assert.Equal(("cleric", 3, 4), (current!.ProfessionCode, current.Level, current.Experience));
        Assert.Equal(TalentRules.EffectiveMaxHp(current), current.Hp);
        Assert.Empty(await test.Db.CharacterItemStacks.ToListAsync()); // Zero stock is allowed, never fabricated.
    }

    [Theory]
    [InlineData("Join")]
    [InlineData("Assign")]
    public async Task BothAdditionalAdmissionPathsApplyAndRemember(string action)
    {
        await using var test = await Fixture.CreateAsync();
        var (created, createError) = await test.CreateRoomAsync(1, null, isPublic: true);
        Assert.Null(createError);
        var userId = action == "Join" ? 2 : 1;
        var character = await test.AddCharacterAsync(userId, "追加角色");
        var formation = await test.FormationAsync(character.Id);
        var result = action == "Join"
            ? await test.Rooms.JoinRoomAsync(created!.RoomId, new JoinRoomRequest { CharacterId = character.Id,
                SlotIndex = 2, LoadoutSelection = Select(formation), RequestId = "join-test" }, "token-2")
            : await test.Rooms.AssignSlotAsync(created!.RoomId, new AssignRoomSlotRequest { CharacterId = character.Id,
                SlotIndex = 2, LoadoutSelection = Select(formation), RequestId = "assign-test" }, "token-1");
        Assert.Null(result.Error);
        await test.AssertAppliedAsync(character.Id, formation, created.RoomId);
        var advancedRoom = (await test.Db.Rooms.FindAsync(created.RoomId))!;
        advancedRoom.Status = RoomStatus.Preparing; advancedRoom.RoundNumber = 1;
        await test.Db.SaveChangesAsync();
        var repeated = action == "Join"
            ? await test.Rooms.JoinRoomAsync(created.RoomId, new JoinRoomRequest { CharacterId = character.Id,
                SlotIndex = 2, LoadoutSelection = Select(formation), RequestId = "join-test" }, "token-2")
            : await test.Rooms.AssignSlotAsync(created.RoomId, new AssignRoomSlotRequest { CharacterId = character.Id,
                SlotIndex = 2, LoadoutSelection = Select(formation), RequestId = "assign-test" }, "token-1");
        Assert.Null(repeated.Error);
        Assert.Equal(created.RoomId, repeated.Detail!.RoomId);
        Assert.Equal(1, (await test.Db.CharacterBattleFormationPreferences.SingleAsync(p => p.CharacterId == character.Id)).Version);
    }

    [Fact]
    public async Task ExplicitCharacterWinsOverAccountActiveCharacter()
    {
        await using var test = await Fixture.CreateAsync();
        var other = await test.AddCharacterAsync(1, "明确出战角色");
        var formation = await test.FormationAsync(other.Id);
        Assert.Equal(1, (await test.Db.Users.FindAsync(1))!.ActiveCharacterId);
        var (detail, error) = await test.CreateRoomAsync(other.Id, formation);
        Assert.Null(error);
        Assert.Equal(other.Id, Assert.Single(detail!.Slots, s => s.IsOccupied).CharacterId);
        Assert.False(await test.Db.CharacterActivities.AnyAsync(a => a.CharacterId == 1));
        await test.AssertAppliedAsync(other.Id, formation, detail.RoomId);
    }

    [Fact]
    public async Task OccupiedSlotRollsBackEveryAppliedChoiceAndProfessionArchive()
    {
        await using var test = await Fixture.CreateAsync();
        var (created, _) = await test.CreateRoomAsync(1, null);
        var character = await test.AddCharacterAsync(1, "未入场角色");
        var formation = await test.FormationAsync(character.Id);
        var before = CombatLoadoutCodec.ConfigurationHash(await test.Loadouts.CaptureAsync(character.Id));
        var version = character.Version;
        var result = await test.Rooms.AssignSlotAsync(created!.RoomId, new AssignRoomSlotRequest
        {
            CharacterId = character.Id, SlotIndex = 1, LoadoutSelection = Select(formation), RequestId = "occupied"
        }, "token-1");
        Assert.Equal("SlotOccupied", result.Error);
        await using var restored = test.NewDb();
        Assert.Equal(before, CombatLoadoutCodec.ConfigurationHash(await test.MakeLoadouts(restored).CaptureAsync(character.Id)));
        var unchanged = (await restored.Characters.FindAsync(character.Id))!;
        Assert.Equal(("knight", 5, 9, version), (unchanged.ProfessionCode, unchanged.Level, unchanged.Experience, unchanged.Version));
        Assert.False(await restored.CharacterActivities.AnyAsync(a => a.CharacterId == character.Id));
        Assert.False(await restored.CharacterCombatProfessions.AnyAsync(p => p.CharacterId == character.Id && p.ProfessionCode == "knight"));
        Assert.False(await restored.CharacterBattleFormationPreferences.AnyAsync(p => p.CharacterId == character.Id));
        Assert.False(await restored.BattleAdmissionReceipts.AnyAsync(r => r.RequestId == "occupied"));
    }

    [Fact]
    public async Task InvalidExplicitFormationFailsWithoutPartialAdmission()
    {
        await using var test = await Fixture.CreateAsync();
        var formation = await test.FormationAsync(1);
        var saved = await test.Db.CharacterBattleFormations.Include(f => f.Weapons).SingleAsync(f => f.Id == formation.Id);
        saved.Weapons.Single(w => w.SlotIndex == 1).WeaponId = 99999;
        await test.Db.SaveChangesAsync();
        var before = CombatLoadoutCodec.ConfigurationHash(await test.Loadouts.CaptureAsync(1));
        var result = await test.CreateRoomAsync(1, formation);
        Assert.Equal("WeaponNotOwned", result.Error);
        Assert.Equal(before, CombatLoadoutCodec.ConfigurationHash(await test.Loadouts.CaptureAsync(1)));
        Assert.Empty(await test.Db.Rooms.ToListAsync());
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
        Assert.Empty(await test.Db.BattleAdmissionReceipts.ToListAsync());
        Assert.Empty(await test.Db.CharacterBattleFormationPreferences.ToListAsync());
    }

    [Fact]
    public async Task SameRequestReturnsSameAdmissionAndDifferentSelectionCannotReuseIt()
    {
        await using var test = await Fixture.CreateAsync();
        var formation = await test.FormationAsync(1);
        var (first, error) = await test.CreateRoomAsync(1, formation, requestId: "retry");
        Assert.Null(error);
        var version = (await test.Db.Characters.FindAsync(1))!.Version;
        var remembered = (await test.Db.CharacterBattleFormationPreferences.SingleAsync()).Version;
        await using var restored = test.NewDb();
        var rooms = test.MakeRooms(restored);
        var (second, retryError) = await rooms.CreateRoomAsync(test.DungeonId, null, "token-1", characterId: 1,
            loadoutSelection: Select(formation), requestId: "retry");
        Assert.Null(retryError);
        Assert.Equal(first!.RoomId, second!.RoomId);
        Assert.Single(await restored.Rooms.ToListAsync());
        Assert.Single(await restored.BattleAdmissionReceipts.ToListAsync());
        Assert.Equal(version, (await restored.Characters.FindAsync(1))!.Version);
        Assert.Equal(remembered, (await restored.CharacterBattleFormationPreferences.SingleAsync()).Version);
        var conflicting = await rooms.CreateRoomAsync(test.DungeonId, null, "token-1", characterId: 1,
            loadoutSelection: new LoadoutSelection { Mode = "Current" }, requestId: "retry");
        Assert.Equal("RequestIdConflict", conflicting.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedReservationUsesFrozenChoicesAfterPresetChanges(bool deletePreset)
    {
        await using var test = await Fixture.CreateAsync();
        var (created, _) = await test.CreateRoomAsync(1, null);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.Status = RoomStatus.Cooldown; room.RoundNumber = 1;
        var character = await test.AddCharacterAsync(1, "预约角色");
        var formation = await test.FormationAsync(character.Id);
        var (queued, error) = await test.Rooms.SubmitOperationAsync(room.Id, new SubmitRoomOperationRequest
        {
            Kind = RoomOperationKind.Assign, CharacterId = character.Id, SlotIndex = 2,
            LoadoutSelection = Select(formation), RequestId = "reservation"
        }, "token-1");
        Assert.Null(error);
        Assert.Equal("Pending", Assert.Single(queued!.Operations).Status);
        Assert.Equal("knight", character.ProfessionCode);
        Assert.False(await test.Db.CharacterBattleFormationPreferences.AnyAsync(p => p.CharacterId == character.Id));
        var changed = CombatLoadoutCodec.Clone(formation.Loadout);
        changed.SoulImprintId = null; changed.SoulAutoUseEnabled = false;
        var saved = await test.Formations.SaveAsync("token-1", character.Id, formation.Id, new SaveFormationRequest
        {
            Name = formation.Name, GroupElement = formation.GroupElement, Position = formation.Position,
            ExpectedVersion = formation.Version, Loadout = changed
        });
        Assert.Null(saved.Error);
        if (deletePreset)
            Assert.Null((await test.Formations.DeleteAsync("token-1", character.Id, formation.Id, saved.Response!.Version)).Error);
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await using var resumed = test.NewDb();
        await test.MakeRooms(resumed).ProcessPendingOperationsAsync(room.Id);
        var operation = await resumed.RoomOperations.SingleAsync();
        Assert.Equal("Completed", operation.Status);
        var slot = await resumed.RoomSlots.SingleAsync(s => s.CharacterId == character.Id);
        Assert.Equal(formation.Version, slot.SourceFormationVersion);
        Assert.Equal(formation.Loadout.SoulImprintId, (await resumed.CharacterSoulImprints.SingleAsync(s =>
            s.CharacterId == character.Id && s.EquippedSlotIndex == 1)).Id);
        Assert.Equal(CombatLoadoutCodec.ConfigurationHash(formation.Loadout),
            CombatLoadoutCodec.ConfigurationHash(await test.MakeLoadouts(resumed).CaptureAsync(character.Id)));
        Assert.Equal(!deletePreset, await resumed.CharacterBattleFormationPreferences.AnyAsync(p => p.CharacterId == character.Id));
    }

    [Fact]
    public async Task SwappingOccupiedSlotsMovesSourceAndTemporaryPoliciesWithCharacter()
    {
        await using var test = await Fixture.CreateAsync();
        var first = await test.FormationAsync(1);
        var (created, _) = await test.CreateRoomAsync(1, first);
        var other = await test.AddCharacterAsync(1, "第二角色");
        var second = await test.FormationAsync(other.Id);
        Assert.Null((await test.Rooms.AssignSlotAsync(created!.RoomId, new AssignRoomSlotRequest
        { CharacterId = other.Id, SlotIndex = 2, LoadoutSelection = Select(second) }, "token-1")).Error);
        var original = await test.Db.RoomSlots.SingleAsync(s => s.CharacterId == 1);
        original.AutoPolicyOverridesJson = BattleAutoPolicyResolver.WithSoul(original, false);
        var policy = original.AutoPolicyOverridesJson;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Rooms.AssignSlotAsync(created.RoomId, new AssignRoomSlotRequest
        { CharacterId = 1, SlotIndex = 2 }, "token-1")).Error);
        var moved = await test.Db.RoomSlots.SingleAsync(s => s.CharacterId == 1);
        Assert.Equal((2, first.Id, policy), (moved.SlotIndex, moved.SourceFormationId, moved.AutoPolicyOverridesJson));
        var displaced = await test.Db.RoomSlots.SingleAsync(s => s.CharacterId == other.Id);
        Assert.Equal((1, second.Id), (displaced.SlotIndex, displaced.SourceFormationId));
    }

    [Fact]
    public async Task AutomaticRepeatUsesAppliedChoicesAfterSavedPresetChanges()
    {
        await using var test = await Fixture.CreateAsync();
        var formation = await test.FormationAsync(1);
        var (created, error) = await test.CreateRoomAsync(1, formation, repeat: true);
        Assert.Null(error);
        var originalHash = CombatLoadoutCodec.ConfigurationHash(await test.Loadouts.CaptureAsync(1));
        var changed = CombatLoadoutCodec.Clone(formation.Loadout);
        changed.SoulImprintId = null;
        Assert.Null((await test.Formations.SaveAsync("token-1", 1, formation.Id, new SaveFormationRequest
        {
            Name = formation.Name, GroupElement = formation.GroupElement, Position = formation.Position,
            ExpectedVersion = formation.Version, Loadout = changed
        })).Error);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.Status = RoomStatus.BattleOver; room.RoundNumber = 3;
        room.BattleEndedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        (await test.Db.Monsters.FindAsync(room.MonsterId))!.Hp = 0;
        await test.Db.SaveChangesAsync();
        await using var resumed = test.NewDb();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var battle = new BattleService(resumed, new UserService(resumed, progression, skills), test.Potions, skills,
            RewardTestFactory.CreateService(resumed, progression), roomService: test.MakeRooms(resumed),
            loadoutIntegrity: new BattleLoadoutIntegrityService(resumed, test.MakeLoadouts(resumed)));
        var result = await battle.SyncRoomAsync(room.Id);
        Assert.Null(result.Error);
        Assert.Equal(2, (await resumed.Rooms.FindAsync(room.Id))!.RunSequence);
        Assert.Equal(originalHash, CombatLoadoutCodec.ConfigurationHash(await test.MakeLoadouts(resumed).CaptureAsync(1)));
        Assert.Equal(formation.Version, (await resumed.RoomSlots.SingleAsync(s => s.CharacterId == 1)).SourceFormationVersion);
    }

    private static LoadoutSelection Select(FormationResponse formation) => new()
    { Mode = "SavedFormation", FormationId = formation.Id, ExpectedVersion = formation.Version };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "formation-admission-" + Guid.NewGuid().ToString("N") + ".db");
        public GameDbContext Db { get; private set; } = null!;
        public int DungeonId { get; private set; }
        public string DungeonCode { get; private set; } = "";
        public ConsumableCatalog Potions { get; } = ConsumableTestFactory.Create();
        private readonly SkillCatalog skills = SkillTestFactory.Create();
        private readonly SoulImprintCatalog souls = SoulImprintTestFactory.Create();
        private readonly WeaponCatalog weapons = new(Options.Create(new WeaponOptions
        {
            Items = [new WeaponTemplateOptions { Code = "test", Name = "测试武器", Element = ElementType.Water, Attack = 10, MaxHp = 20 }],
            StarterPacks = new() { ["knight"] = ["test"], ["cleric"] = ["test"] }
        }));
        public CombatLoadoutService Loadouts => MakeLoadouts(Db);
        public FormationService Formations => new(Db, Users(Db), Loadouts);
        public RoomService Rooms => MakeRooms(Db);
        public GameDbContext NewDb() => new(new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        private UserService Users(GameDbContext db) => new(db, ProgressionTestFactory.Create(), skills);
        public CombatLoadoutService MakeLoadouts(GameDbContext db) => new(db, skills, weapons, Potions, souls);
        public FormationService MakeFormations(GameDbContext db) => new(db, Users(db), MakeLoadouts(db));
        public RoomService MakeRooms(GameDbContext db, DungeonDepthCatalog? depths = null) => new(db, Users(db), ProgressionTestFactory.Create(), Potions, skills,
            RewardTestFactory.CreateService(db, ProgressionTestFactory.Create()), weaponCatalog: weapons,
            soulImprintCatalog: souls, depthCatalog: depths, loadouts: MakeLoadouts(db), loadoutIntegrity: new(db, MakeLoadouts(db)));

        public static async Task<Fixture> CreateAsync()
        {
            var test = new Fixture(); test.Db = test.NewDb();
            await test.Db.Database.EnsureCreatedAsync();
            test.Db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new User { Id = 2, UserName = "guest", PasswordHash = "x" },
                new UserLoginSession { UserId = 1, Token = "token-1", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                new UserLoginSession { UserId = 2, Token = "token-2", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await test.Db.SaveChangesAsync();
            await DbInitializer.EnsureDefaultDungeonsAsync(test.Db);
            var dungeon = await test.Db.Dungeons.Where(d => d.IsVisible && d.DungeonKind == "Hunt")
                .OrderBy(d => d.SortOrder).FirstAsync();
            test.DungeonId = dungeon.Id; test.DungeonCode = dungeon.Code;
            await test.AddCharacterAsync(1, "原角色");
            return test;
        }

        public async Task<Character> AddCharacterAsync(int userId, string name)
        {
            var character = new Character { UserId = userId, Name = name, ProfessionCode = "knight", Level = 5,
                Experience = 9, Attack = 20, MaxHp = 100, Hp = 80 };
            Db.Characters.Add(character); await Db.SaveChangesAsync();
            Db.AddRange(new CharacterWeapon { CharacterId = character.Id, WeaponCode = "test", Name = "原武器",
                Element = ElementType.Fire, Attack = 1, MaxHp = 1, EquippedSlotIndex = 1 },
                new CharacterWeapon { CharacterId = character.Id, WeaponCode = "test", Name = "新武器",
                    Element = ElementType.Water, Attack = 10, MaxHp = 20 },
                new CharacterSoulImprint { CharacterId = character.Id, SoulImprintCode = "deep-core" },
                new CharacterCombatProfession { CharacterId = character.Id, ProfessionCode = "cleric", Level = 3, Experience = 4 },
                new CharacterSkillSlot { CharacterId = character.Id, SlotIndex = 1, SkillCode = "knight-strike" });
            await Db.SaveChangesAsync(); return character;
        }

        public async Task<FormationResponse> FormationAsync(int characterId)
        {
            var weapon = await Db.CharacterWeapons.SingleAsync(w => w.CharacterId == characterId && w.EquippedSlotIndex == null);
            var soul = await Db.CharacterSoulImprints.SingleAsync(s => s.CharacterId == characterId);
            var (formation, error) = await Formations.CreateAsync("token-" + (await Db.Characters.FindAsync(characterId))!.UserId,
                characterId, new CreateFormationRequest
                {
                    Name = "牧师配置", GroupElement = ElementType.Water, Position = 1,
                    Loadout = new CombatLoadoutDefinition
                    {
                        ProfessionCode = "cleric", Weapons = [new() { SlotIndex = 1, WeaponId = weapon.Id }],
                        Skills = [new() { SlotIndex = 1, SkillCode = "cleric-heal", AutoUseEnabled = true, AutoHpThresholdPercent = 40 }],
                        Consumables = [new() { SlotIndex = 1, ItemCode = "minor-healing-potion", AutoUseEnabled = true, AutoHpThresholdPercent = 50 },
                            new() { SlotIndex = 3, ItemCode = "northshire-battle-draught" }],
                        SoulImprintId = soul.Id, SoulAutoUseEnabled = true
                    }
                });
            Assert.Null(error); return formation!;
        }

        public async Task<FormationResponse> SecondFormationAsync(int characterId)
        {
            var result = await Formations.CreateAsync("token-1", characterId, new CreateFormationRequest
            {
                Name = "原职业完整配置", GroupElement = ElementType.Fire, Position = 1,
                Loadout = await Loadouts.CaptureAsync(characterId)
            });
            Assert.Null(result.Error);
            return result.Response!;
        }

        public Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(int characterId, FormationResponse? formation,
            bool isPublic = false, bool repeat = false, string? requestId = null) => Rooms.CreateRoomAsync(DungeonId, null, "token-1",
                isRepeatBattle: repeat, isPublic: isPublic, characterId: characterId,
                loadoutSelection: formation is null ? new() { Mode = "Current" } : Select(formation), requestId: requestId);

        public async Task AssertAppliedAsync(int characterId, FormationResponse formation, int roomId)
        {
            var actual = await Loadouts.CaptureAsync(characterId);
            Assert.Equal(CombatLoadoutCodec.ConfigurationHash(formation.Loadout), CombatLoadoutCodec.ConfigurationHash(actual));
            var slot = await Db.RoomSlots.SingleAsync(s => s.CharacterId == characterId);
            Assert.Equal((roomId, formation.Id, formation.Version), (slot.RoomId, slot.SourceFormationId, slot.SourceFormationVersion));
            Assert.Equal(CombatLoadoutCodec.ConfigurationHash(actual),
                CombatLoadoutCodec.ConfigurationHash(CombatLoadoutCodec.Deserialize(slot.AppliedLoadoutJson!)));
            var preference = await Db.CharacterBattleFormationPreferences.FindAsync(characterId, DungeonCode, 1);
            Assert.Equal(formation.Id, preference!.FormationId);
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); File.Delete(path); }
    }
}
