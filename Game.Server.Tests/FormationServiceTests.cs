using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class FormationServiceTests
{
    [Fact]
    public async Task MigrationAddsChoiceStorageAndKeepsExistingGrowth()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260929010000_CharacterFirstHuntWeaponClaims");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Users(Id,UserName,PasswordHash) VALUES(1,'player','unused')");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Characters(Id,UserId,Name,Hp,MaxHp,Attack,Level,Experience) VALUES(1,1,'hero',20,100,10,8,9)");
        await db.Database.MigrateAsync();
        var character = await db.Characters.SingleAsync();
        Assert.Equal((8, 9, 20), (character.Level, character.Experience, character.Hp));
        db.CharacterBattleFormations.Add(new CharacterBattleFormation { CharacterId = 1, Position = 1, Name = "test" });
        await db.SaveChangesAsync();
        Assert.Single(await db.CharacterBattleFormations.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task FormationMigrationCanDowngradeAndReapplyWithoutChangingExistingCharacters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        db.Users.Add(new User { Id = 1, UserName = "player", PasswordHash = "unused" });
        db.Characters.Add(new Character { Id = 1, UserId = 1, Name = "hero", Level = 8, Experience = 9, Hp = 20, MaxHp = 100, Attack = 10 });
        await db.SaveChangesAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260930040000_AddEconomicRequestResults");
        Assert.DoesNotContain("20261002010000_AddBattleFormations", await db.Database.GetAppliedMigrationsAsync());
        var prior = await db.Characters.AsNoTracking().SingleAsync();
        Assert.Equal((8, 9, 20), (prior.Level, prior.Experience, prior.Hp));
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.CharacterBattleFormations.ToListAsync());
        Assert.Empty(await db.BattleAdmissionReceipts.ToListAsync());
    }

    [Fact]
    public async Task SaveCopyAndDeleteKeepCurrentConfigurationIndependent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (created, error) = await fixture.Service.CreateAsync("token", fixture.Character.Id, new CreateFormationRequest
        { Name = "火队", GroupElement = ElementType.Fire, Position = 1, FromCurrent = true });
        Assert.Null(error); Assert.NotNull(created);
        var source = created!;
        source.Loadout.Consumables.Add(new FormationConsumableChoice { SlotIndex = 3, ItemCode = "northshire-battle-draught" });
        var save = await fixture.Service.SaveAsync("token", fixture.Character.Id, source.Id, new SaveFormationRequest
        { Name = "火队更新", GroupElement = ElementType.Fire, Position = 1, ExpectedVersion = source.Version, Loadout = source.Loadout });
        // Capture already contains all three slots; append was an invalid duplicate and must not corrupt storage.
        Assert.Equal("InvalidSlotIndex", save.Error);
        Assert.Equal(source.Version, (await fixture.Db.CharacterBattleFormations.FindAsync(source.Id))!.Version);
        source.Loadout.Consumables.RemoveAt(source.Loadout.Consumables.Count - 1);
        source.Loadout.Consumables.Single(x => x.SlotIndex == 3).ItemCode = "northshire-battle-draught";
        save = await fixture.Service.SaveAsync("token", fixture.Character.Id, source.Id, new SaveFormationRequest
        { Name = "火队更新", GroupElement = ElementType.Fire, Position = 1, ExpectedVersion = source.Version, Loadout = source.Loadout });
        Assert.Null(save.Error); Assert.Equal(2, save.Response!.Version);
        Assert.Empty(await fixture.Db.CharacterConsumableSlots.ToListAsync());
        Assert.Equal((8, 9, 20), (fixture.Character.Level, fixture.Character.Experience, fixture.Character.Hp));
        var copied = await fixture.Service.CopyAsync("token", fixture.Character.Id, source.Id, new CopyFormationRequest
        { ExpectedVersion = 2, Name = "备用", GroupElement = ElementType.Water, Position = 1 });
        Assert.Null(copied.Error); Assert.NotEqual(source.Id, copied.Response!.Id);
        Assert.Equal("northshire-battle-draught", copied.Response.Loadout.Consumables.Single(x => x.SlotIndex == 3).ItemCode);
        await fixture.Service.SetDefaultAsync("token", fixture.Character.Id, new SetDefaultFormationRequest { FormationId = source.Id, ExpectedVersion = 2 });
        fixture.Db.CharacterBattleFormationPreferences.Add(new CharacterBattleFormationPreference
        { CharacterId = fixture.Character.Id, DungeonCode = "encounter", DepthLevel = 1, FormationId = source.Id });
        await fixture.Db.SaveChangesAsync();
        var deleted = await fixture.Service.DeleteAsync("token", fixture.Character.Id, source.Id, 2);
        Assert.Null(deleted.Error); Assert.Null(deleted.Response!.DefaultFormationId);
        Assert.Empty(await fixture.Db.CharacterBattleFormationPreferences.ToListAsync());
        Assert.Single(deleted.Response.Formations);
        var tombstone = await fixture.Db.CharacterBattleFormations.AsNoTracking().SingleAsync(x => x.Id == source.Id);
        Assert.True(tombstone.IsDeleted);
        Assert.Null(tombstone.SoulImprintId);
        Assert.False(tombstone.SoulAutoUseEnabled);
        Assert.Empty(await fixture.Db.FormationWeaponSlots.Where(x => x.FormationId == source.Id).ToListAsync());
        Assert.Empty(await fixture.Db.FormationSkillSlots.Where(x => x.FormationId == source.Id).ToListAsync());
        Assert.Empty(await fixture.Db.FormationConsumableSlots.Where(x => x.FormationId == source.Id).ToListAsync());
        Assert.Equal("northshire-battle-draught", (await fixture.Db.FormationConsumableSlots.SingleAsync(x => x.FormationId == copied.Response.Id && x.SlotIndex == 3)).ItemCode);
        var replacement = await fixture.Service.CreateAsync("token", fixture.Character.Id, new CreateFormationRequest
        { Name = "新火队", GroupElement = ElementType.Fire, Position = 1, FromCurrent = true });
        Assert.Null(replacement.Error);
    }

    [Fact]
    public async Task BackfillIsIdempotentAndDoesNotHealOrRestoreGrowth()
    {
        await using var fixture = await Fixture.CreateAsync();
        var backfill = new FormationBackfillService(fixture.Db, fixture.Loadouts);
        await backfill.BackfillAsync(); await backfill.BackfillAsync();
        Assert.Single(await fixture.Db.CharacterBattleFormations.ToListAsync());
        Assert.Single(await fixture.Db.CharacterFormationStates.ToListAsync());
        Assert.Equal((8, 9, 20), (fixture.Character.Level, fixture.Character.Experience, fixture.Character.Hp));
    }

    [Fact]
    public async Task FirstOverviewInitializesNewCharactersAndDeletionDoesNotRespawnPreset()
    {
        await using var fixture = await Fixture.CreateAsync();
        var overview = await fixture.Service.GetAsync("token", fixture.Character.Id);
        Assert.Null(overview.Error);
        var initial = Assert.Single(overview.Response!.Formations);
        Assert.Equal("当前配置", initial.Name);
        Assert.Equal(fixture.Character.Id, initial.CharacterId);
        await fixture.Service.DeleteAsync("token", fixture.Character.Id, initial.Id, initial.Version);
        Assert.Empty((await fixture.Service.GetAsync("token", fixture.Character.Id)).Response!.Formations);
    }

    [Fact]
    public async Task PreviewAndApplyUseTargetGrowthAndExplicitSkillsWithoutChangingPreviewedActor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = new CharacterWeapon { CharacterId = fixture.Character.Id, WeaponCode = "blade", Name = "一", MaxHp = 10, Attack = 10, EquippedSlotIndex = 1 };
        var second = new CharacterWeapon { CharacterId = fixture.Character.Id, WeaponCode = "blade", Name = "二", MaxHp = 10, Attack = 15, EquippedSlotIndex = 2 };
        fixture.Db.CharacterWeapons.AddRange(first, second);
        fixture.Db.CharacterCombatProfessions.Add(new CharacterCombatProfession
        {
            CharacterId = fixture.Character.Id, ProfessionCode = "cleric", Level = 12, Experience = 7,
            SkillLoadoutJson = CombatSkillLoadoutCodec.Capture([new CharacterSkillSlot { SlotIndex = 1, SkillCode = "cleric-heal" }])
        });
        await fixture.Db.SaveChangesAsync();
        var definition = new CombatLoadoutDefinition
        {
            ProfessionCode = "cleric",
            Weapons = [new() { SlotIndex = 1, WeaponId = second.Id }, new() { SlotIndex = 2, WeaponId = first.Id }],
            Skills = [new() { SlotIndex = 4, SkillCode = "cleric-wave", AutoUseEnabled = true, AutoHpThresholdPercent = 42 }]
        };
        var version = fixture.Character.Version;
        var preview = await fixture.Loadouts.PreviewAsync(fixture.Character, definition);
        Assert.True(preview.CanDeploy); Assert.Equal(12, preview.Level);
        Assert.Equal(("knight", 8, 9, 20, version), (fixture.Character.ProfessionCode, fixture.Character.Level, fixture.Character.Experience, fixture.Character.Hp, fixture.Character.Version));
        Assert.Equal(1, first.EquippedSlotIndex); Assert.Equal(2, second.EquippedSlotIndex);
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            Assert.Null(await fixture.Loadouts.ApplyAsync(fixture.Character, definition));
            await transaction.CommitAsync();
        }
        Assert.Equal(("cleric", 12, 7), (fixture.Character.ProfessionCode, fixture.Character.Level, fixture.Character.Experience));
        Assert.Equal(2, first.EquippedSlotIndex); Assert.Equal(1, second.EquippedSlotIndex);
        var slots = await fixture.Db.CharacterSkillSlots.OrderBy(x => x.SlotIndex).ToListAsync();
        Assert.Equal(5, slots.Count); Assert.Null(slots[0].SkillCode); Assert.Equal("cleric-wave", slots[3].SkillCode);
        Assert.True(slots[3].AutoUseEnabled); Assert.Equal(42, slots[3].AutoHpThresholdPercent);
        var knight = await fixture.Db.CharacterCombatProfessions.FindAsync(fixture.Character.Id, "knight");
        Assert.Equal((8, 9), (knight!.Level, knight.Experience));
    }

    [Fact]
    public async Task CrossCharacterInstancesAreRejectedAndCallerRollbackRestoresWholeApplication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = new Character { UserId = fixture.Character.UserId, Name = "other", ProfessionCode = "knight" };
        fixture.Db.Characters.Add(other); await fixture.Db.SaveChangesAsync();
        var foreign = new CharacterWeapon { CharacterId = other.Id, WeaponCode = "blade", Name = "他人", MaxHp = 10, Attack = 10 };
        var owned = new CharacterWeapon { CharacterId = fixture.Character.Id, WeaponCode = "blade", Name = "自己的", MaxHp = 10, Attack = 10, EquippedSlotIndex = 1 };
        fixture.Db.CharacterWeapons.AddRange(foreign, owned); await fixture.Db.SaveChangesAsync();
        var bad = await fixture.Service.CreateAsync("token", fixture.Character.Id, new CreateFormationRequest
        {
            Name = "越权", Position = 1, Loadout = new CombatLoadoutDefinition { ProfessionCode = "knight", Weapons = [new() { SlotIndex = 1, WeaponId = foreign.Id }] }
        });
        Assert.Equal("WeaponNotOwned", bad.Error); Assert.Empty(await fixture.Db.CharacterBattleFormations.ToListAsync());
        var originalVersion = fixture.Character.Version;
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            var selection = new CombatLoadoutDefinition { ProfessionCode = "cleric", Weapons = [new() { SlotIndex = 1, WeaponId = owned.Id }] };
            Assert.Null(await fixture.Loadouts.ApplyAsync(fixture.Character, selection));
            // Simulate a later admission failure after both internal SaveChanges calls.
            await transaction.RollbackAsync();
        }
        fixture.Db.ChangeTracker.Clear();
        var restored = await fixture.Db.Characters.FindAsync(fixture.Character.Id);
        Assert.Equal(("knight", 8, 9, 20, originalVersion), (restored!.ProfessionCode, restored.Level, restored.Experience, restored.Hp, restored.Version));
        Assert.Empty(await fixture.Db.CharacterSkillSlots.ToListAsync());
        Assert.Empty(await fixture.Db.CharacterConsumableSlots.ToListAsync());
        Assert.Empty(await fixture.Db.CharacterCombatProfessions.ToListAsync());
        Assert.Equal(1, (await fixture.Db.CharacterWeapons.FindAsync(owned.Id))!.EquippedSlotIndex);
    }

    [Fact]
    public async Task ApplyRetriesReturnOriginalReceiptAndUnknownSkillCacheVersionRollsBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        var weapon = new CharacterWeapon { CharacterId = fixture.Character.Id, WeaponCode = "blade", Name = "剑", MaxHp = 10, Attack = 10, EquippedSlotIndex = 1 };
        fixture.Db.CharacterWeapons.Add(weapon); await fixture.Db.SaveChangesAsync();
        var created = await fixture.Service.CreateAsync("token", fixture.Character.Id, new CreateFormationRequest
        { Name = "出战", GroupElement = ElementType.Fire, Position = 1, FromCurrent = true });
        Assert.Null(created.Error);
        var request = new ApplyFormationRequest
        { ExpectedVersion = created.Response!.Version, ExpectedCharacterVersion = fixture.Character.Version, RequestId = "apply-once" };
        var applied = await fixture.Service.ApplyAsync("token", fixture.Character.Id, created.Response.Id, request);
        Assert.Null(applied.Error); Assert.False(applied.Response!.CurrentIsModified);
        fixture.Character.Version++; await fixture.Db.SaveChangesAsync();
        var retried = await fixture.Service.ApplyAsync("token", fixture.Character.Id, created.Response.Id, request);
        Assert.Null(retried.Error); Assert.Equal(applied.Response.CharacterVersion, retried.Response!.CharacterVersion);
        var conflict = await fixture.Service.ApplyAsync("token", fixture.Character.Id, created.Response.Id + 1, request);
        Assert.Equal("RequestIdConflict", conflict.Error);
        var archived = await fixture.Db.CharacterCombatProfessions.FindAsync(fixture.Character.Id, "knight");
        archived!.SkillLoadoutJson = "{\"SchemaVersion\":999,\"Slots\":[]}";
        await fixture.Db.SaveChangesAsync();
        var before = fixture.Character.Version;
        var rejected = await fixture.Service.ApplyAsync("token", fixture.Character.Id, created.Response.Id,
            new ApplyFormationRequest { ExpectedVersion = 1, ExpectedCharacterVersion = before, RequestId = "future-cache" });
        Assert.Equal("UnsupportedSkillLoadoutVersion", rejected.Error);
        var restored = await fixture.Db.Characters.FindAsync(fixture.Character.Id);
        Assert.Equal((8, 9, before), (restored!.Level, restored.Experience, restored.Version));
        Assert.Equal("{\"SchemaVersion\":999,\"Slots\":[]}", (await fixture.Db.CharacterCombatProfessions.FindAsync(restored.Id, "knight"))!.SkillLoadoutJson);
        Assert.Single(await fixture.Db.BattleAdmissionReceipts.ToListAsync());
    }

    [Fact]
    public async Task MalformedNestedDraftsAreRejectedWithoutWritingAnything()
    {
        await using var fixture = await Fixture.CreateAsync();
        var malformed = new CombatLoadoutDefinition[]
        {
            new() { ProfessionCode = "knight", Weapons = null! },
            new() { ProfessionCode = "knight", Skills = [null!] },
            new() { ProfessionCode = "knight", Consumables = [null!] }
        };
        foreach (var definition in malformed)
        {
            var result = await fixture.Service.CreateAsync("token", fixture.Character.Id,
                new CreateFormationRequest { Name = "草稿", Position = 1, Loadout = definition });
            Assert.NotNull(result.Error);
        }
        Assert.Empty(await fixture.Db.CharacterBattleFormations.ToListAsync());
        Assert.Equal((8, 9, 20), (fixture.Character.Level, fixture.Character.Experience, fixture.Character.Hp));
    }

    [Fact]
    public async Task ConfiguredCapacityAllowsExpansionAndPreservesExistingPositionsWhenReduced()
    {
        await using var fixture = await Fixture.CreateAsync(8);
        var create = await fixture.Service.CreateAsync("token", fixture.Character.Id, new CreateFormationRequest
        { Name = "第八套", Position = 8, FromCurrent = true });
        Assert.Null(create.Error);
        var tooMany = await fixture.Service.CreateAsync("token", fixture.Character.Id, new CreateFormationRequest
        { Name = "超限", Position = 9, FromCurrent = true });
        Assert.Equal("InvalidFormationPosition", tooMany.Error);
        var reduced = new FormationService(fixture.Db, fixture.Users, fixture.Loadouts,
            Options.Create(new FormationOptions { PositionsPerElement = 2 }));
        var overview = await reduced.GetAsync("token", fixture.Character.Id);
        Assert.Null(overview.Error); Assert.Equal(8, overview.Response!.PositionsPerElement);
        Assert.Contains(overview.Response.Formations, x => x.Id == create.Response!.Id && x.Position == 8);
        var saved = await reduced.SaveAsync("token", fixture.Character.Id, create.Response!.Id, new SaveFormationRequest
        { Name = "保留第八套", Position = 8, ExpectedVersion = create.Response.Version, Loadout = create.Response.Loadout });
        Assert.Null(saved.Error);
    }

    [Fact]
    public async Task FormationCapturesCopiesAndAppliesSoulAutoCondition()
    {
        await using var fixture = await Fixture.CreateAsync();
        var soul = new CharacterSoulImprint { CharacterId = fixture.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = 1, AutoUseEnabled = true, AutoConditionOverride = "SelfHpBelowThreshold", AutoHpThresholdPercent = 42 };
        fixture.Db.AddRange(soul, new CharacterWeapon { CharacterId = fixture.Character.Id, WeaponCode = "blade",
            Name = "主武器", EquippedSlotIndex = 1, Attack = 10, MaxHp = 10 });
        await fixture.Db.SaveChangesAsync();
        var (created, error) = await fixture.Service.CreateAsync("token", fixture.Character.Id,
            new() { Name = "魂印编队", Position = 1, FromCurrent = true });
        Assert.Null(error);
        Assert.Equal((soul.Id, true, "SelfHpBelowThreshold", 42), (created!.Loadout.SoulImprintId,
            created.Loadout.SoulAutoUseEnabled, created.Loadout.SoulAutoConditionOverride, created.Loadout.SoulAutoHpThresholdPercent));
        var previousHash = CombatLoadoutCodec.ConfigurationHash(created.Loadout);
        var previousChoices = CombatLoadoutCodec.ChoiceHash(created.Loadout);
        created.Loadout.SoulAutoConditionOverride = "MonsterHpBelowThreshold";
        created.Loadout.SoulAutoHpThresholdPercent = 35;
        Assert.NotEqual(previousHash, CombatLoadoutCodec.ConfigurationHash(created.Loadout));
        Assert.Equal(previousChoices, CombatLoadoutCodec.ChoiceHash(created.Loadout));
        var (saved, saveError) = await fixture.Service.SaveAsync("token", fixture.Character.Id, created.Id,
            new() { Name = created.Name, GroupElement = created.GroupElement, Position = created.Position,
                ExpectedVersion = created.Version, Loadout = created.Loadout });
        Assert.Null(saveError);
        var (copied, copyError) = await fixture.Service.CopyAsync("token", fixture.Character.Id, created.Id,
            new() { Name = "备用魂印", Position = 2, ExpectedVersion = saved!.Version });
        Assert.Null(copyError);
        Assert.Equal(("MonsterHpBelowThreshold", 35), (copied!.Loadout.SoulAutoConditionOverride, copied.Loadout.SoulAutoHpThresholdPercent));
        var stored = await fixture.Db.CharacterBattleFormations.SingleAsync(f => f.Id == copied.Id);
        Assert.Equal(35, CombatLoadoutService.FromFormation(stored).SoulAutoHpThresholdPercent);
        var (_, applyError) = await fixture.Service.ApplyAsync("token", fixture.Character.Id, copied.Id,
            new() { ExpectedVersion = copied.Version, ExpectedCharacterVersion = fixture.Character.Version, RequestId = "soul-condition" });
        Assert.Null(applyError);
        Assert.Equal(("MonsterHpBelowThreshold", 35), (soul.AutoConditionOverride, soul.AutoHpThresholdPercent));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required GameDbContext Db { get; init; }
        public required Character Character { get; init; }
        public required CombatLoadoutService Loadouts { get; init; }
        public required FormationService Service { get; init; }
        public required UserService Users { get; init; }
        public static async Task<Fixture> CreateAsync(int positionsPerElement = 6)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var user = new User { UserName = "player", PasswordHash = "unused" }; db.Users.Add(user); await db.SaveChangesAsync();
            db.UserLoginSessions.Add(new UserLoginSession { UserId = user.Id, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddHours(1) });
            var actor = new Character { UserId = user.Id, Name = "hero", ProfessionCode = "knight", Level = 8, Experience = 9, Hp = 20, MaxHp = 100, Attack = 10 };
            db.Characters.Add(actor); await db.SaveChangesAsync();
            var skills = new SkillCatalog(Options.Create(new SkillOptions
            {
                Professions = [new ProfessionOptions { Code = "knight", Name = "骑士", StartingSkills = ["knight-hit"] }, new ProfessionOptions { Code = "cleric", Name = "牧师", StartingSkills = ["cleric-heal"] }],
                Abilities =
                [
                    new CombatSkillOptions { Code = "knight-hit", ProfessionCode = "knight", Name = "斩击", Description = "伤害", EffectType = "Damage", Power = 10, UnlockLevel = 1 },
                    new CombatSkillOptions { Code = "cleric-heal", ProfessionCode = "cleric", Name = "治疗", Description = "治疗", EffectType = "Heal", Power = 10, UnlockLevel = 1 },
                    new CombatSkillOptions { Code = "cleric-wave", ProfessionCode = "cleric", Name = "光波", Description = "伤害", EffectType = "Damage", Power = 10, UnlockLevel = 1 }
                ]
            }));
            var weapons = new WeaponCatalog(Options.Create(new WeaponOptions
            {
                Items = [new WeaponTemplateOptions { Code = "blade", Name = "测试剑", Attack = 10, MaxHp = 10 }],
                StarterPacks = new Dictionary<string, List<string>> { ["knight"] = ["blade"] }
            }));
            var loadouts = new CombatLoadoutService(db, skills, weapons, ConsumableTestFactory.Create(), SoulImprintTestFactory.Create());
            var progression = new ProgressionService(Options.Create(new ProgressionOptions { MaximumLevel = 30, ExperienceToNextLevel = Enumerable.Repeat(10, 29).ToList() }));
            var users = new UserService(db, progression, skills);
            return new Fixture { Connection = connection, Db = db, Character = actor, Loadouts = loadouts, Users = users,
                Service = new FormationService(db, users, loadouts, Options.Create(new FormationOptions { PositionsPerElement = positionsPerElement })) };
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
