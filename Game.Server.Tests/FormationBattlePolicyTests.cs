using Game.Server.Data;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class FormationBattlePolicyTests
{
    [Theory]
    [InlineData(RoomStatus.NotStarted)]
    [InlineData(RoomStatus.BattleOver)]
    [InlineData(RoomStatus.WaveTransition)]
    public async Task OccupiedCharacterRemainsLockedAcrossRunBoundaries(RoomStatus status)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AddRange(new Room { Id = 1, Status = status }, new RoomSlot { RoomId = 1, SlotIndex = 1, CharacterId = 1 });
        await db.SaveChangesAsync();
        Assert.Equal("LoadoutLocked", await CombatLoadoutMutationPolicy.LockErrorAsync(db, 1));
        db.RoomSlots.Remove(await db.RoomSlots.SingleAsync());
        await db.SaveChangesAsync();
        Assert.Null(await CombatLoadoutMutationPolicy.LockErrorAsync(db, 1));
    }

    [Fact]
    public void OverridesSurviveSerializationWithoutChangingBaseOrOtherSkills()
    {
        var slot = new RoomSlot();
        var first = new CharacterSkillSlot { SlotIndex = 1, SkillCode = "a", AutoUseEnabled = false,
            AutoConditionOverride = "Always", AutoHpThresholdPercent = 70 };
        var second = new CharacterSkillSlot { SlotIndex = 2, SkillCode = "b", AutoUseEnabled = true };
        slot.AutoPolicyOverridesJson = BattleAutoPolicyResolver.WithSkill(slot, 1, true, null, 40);
        slot.AutoPolicyOverridesJson = BattleAutoPolicyResolver.WithSoul(slot, false);
        var restored = new RoomSlot { AutoPolicyOverridesJson = slot.AutoPolicyOverridesJson };
        var effective = BattleAutoPolicyResolver.Skill(restored, first);
        Assert.True(effective.AutoUseEnabled);
        Assert.Null(effective.AutoConditionOverride);
        Assert.Equal(40, effective.AutoHpThresholdPercent);
        Assert.False(first.AutoUseEnabled);
        Assert.Equal("Always", first.AutoConditionOverride);
        Assert.True(BattleAutoPolicyResolver.Skill(restored, second).AutoUseEnabled);
        Assert.False(BattleAutoPolicyResolver.SoulAuto(restored, true));
        DungeonRunLifecycleService.ClearRoundState(new Room(), [new BattleParticipant(restored, new Character())]);
        Assert.True(BattleAutoPolicyResolver.Skill(restored, first).AutoUseEnabled);
    }

    [Fact]
    public void UnknownOverrideSchemaCannotSilentlyDiscardSettings()
    {
        var slot = new RoomSlot { AutoPolicyOverridesJson = "{\"SchemaVersion\":9}" };
        Assert.Throws<InvalidOperationException>(() => BattleAutoPolicyResolver.SoulAuto(slot, true));
    }

    [Fact]
    public async Task SkillAutoApiPersistsRoomOverrideAndLeavesBaseConfigurationUntouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options;
        await using (var db = new GameDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new Character { Id = 1, UserId = 1, Name = "骑士", ProfessionCode = "knight" },
                new UserLoginSession { UserId = 1, Token = "policy-token", CreatedAt = DateTime.UtcNow,
                    ExpireAt = DateTime.UtcNow.AddDays(1) },
                new Room { Id = 1, Status = RoomStatus.BattleOver },
                new RoomSlot { RoomId = 1, CharacterId = 1, SlotIndex = 1 },
                new CharacterSkillSlot { CharacterId = 1, SlotIndex = 1, SkillCode = "knight-strike" });
            await db.SaveChangesAsync();
            var skills = SkillTestFactory.Create();
            var service = new SkillService(db, new UserService(db, ProgressionTestFactory.Create(), skills), skills);
            var (response, error) = await service.SetAutoAsync("policy-token", 1, 1,
                new SetSkillAutoRequest { AutoUseEnabled = true, AutoHpThresholdPercent = 40 });
            Assert.Null(error);
            Assert.True(response!.Slots[0].AutoUseEnabled);
            Assert.False((await db.CharacterSkillSlots.SingleAsync()).AutoUseEnabled);
            Assert.Equal(1, (await db.Rooms.SingleAsync()).Version);
            Assert.Equal("LoadoutLocked", (await service.SetSlotAsync("policy-token", 1, 1,
                new SetSkillSlotRequest { SkillCode = "knight-guard" })).Error);
        }
        await using var restored = new GameDbContext(options);
        var baseSlot = await restored.CharacterSkillSlots.SingleAsync();
        var roomSlot = await restored.RoomSlots.SingleAsync();
        Assert.False(baseSlot.AutoUseEnabled);
        Assert.True(BattleAutoPolicyResolver.Skill(roomSlot, baseSlot).AutoUseEnabled);
        Assert.Equal(40, BattleAutoPolicyResolver.Skill(roomSlot, baseSlot).AutoHpThresholdPercent);
    }

    [Fact]
    public async Task DraftReferencesProtectAssetsAndDeletedPresetsReleaseProtection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Characters.Add(new Character { Id = 1, Name = "测试" });
        var formation = new CharacterBattleFormation { CharacterId = 1, Position = 1, Name = "未完成草稿",
            SoulImprintId = 88, Weapons = [new FormationWeaponSlot { SlotIndex = 2, WeaponId = 99 }] };
        db.CharacterBattleFormations.Add(formation);
        await db.SaveChangesAsync();
        Assert.Equal("FormationItemReferenced:未完成草稿", await FormationItemReferencePolicy.WeaponsAsync(db, 1, [99]));
        Assert.Equal("FormationItemReferenced:未完成草稿", await FormationItemReferencePolicy.SoulImprintsAsync(db, 1, [88]));
        formation.IsDeleted = true;
        await db.SaveChangesAsync();
        Assert.Null(await FormationItemReferencePolicy.WeaponsAsync(db, 1, [99]));
        Assert.Null(await FormationItemReferencePolicy.SoulImprintsAsync(db, 1, [88]));
    }

    [Fact]
    public async Task RecoveryChecksChoicesButAllowsGrowthAndPersistsAnomalyMarker()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options;
        await using (var db = new GameDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var character = new Character { Id = 1, Name = "骑士", ProfessionCode = "knight" };
            var room = new Room { Id = 1 };
            var slot = new RoomSlot { RoomId = 1, SlotIndex = 1, CharacterId = 1 };
            db.AddRange(character, room, slot);
            await db.SaveChangesAsync();
            var loadouts = Loadouts(db);
            slot.AppliedLoadoutJson = CombatLoadoutCodec.Serialize(await loadouts.CaptureAsync(1));
            slot.AutoPolicyOverridesJson = BattleAutoPolicyResolver.WithSoul(slot, true);
            character.Level = 2;
            character.Experience = 5;
            await db.SaveChangesAsync();
            Assert.Null(await new BattleLoadoutIntegrityService(db, loadouts).EnsureAsync(room, [new(slot, character)]));
            character.ProfessionCode = "cleric";
            await db.SaveChangesAsync();
            Assert.Equal("LoadoutIntegrityMismatch", await new BattleLoadoutIntegrityService(db, loadouts)
                .EnsureAsync(room, [new(slot, character)]));
            await db.SaveChangesAsync();
        }
        await using var restored = new GameDbContext(options);
        Assert.Equal("LoadoutIntegrityMismatch", (await restored.Rooms.SingleAsync()).LoadoutIntegrityError);
    }

    [Fact]
    public async Task ClosingRoomReleasesSourceAndTemporaryStrategyBindings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var room = new Room { Id = 1 };
        var slot = new RoomSlot { RoomId = 1, SlotIndex = 1, CharacterId = 1,
            SourceFormationId = 7, SourceFormationVersion = 3, SourceFormationName = "旧配队",
            AppliedLoadoutJson = "{}", AutoPolicyOverridesJson = "{}" };
        db.AddRange(room, slot);
        await db.SaveChangesAsync();
        await CharacterActivityManager.CloseBattleRoomAsync(db, room, DateTime.UtcNow);
        await db.SaveChangesAsync();
        Assert.Null(slot.CharacterId);
        Assert.Null(slot.SourceFormationId);
        Assert.Null(slot.SourceFormationVersion);
        Assert.Null(slot.SourceFormationName);
        Assert.Null(slot.AppliedLoadoutJson);
        Assert.Null(slot.AutoPolicyOverridesJson);
    }

    private static CombatLoadoutService Loadouts(GameDbContext db) => new(db, SkillTestFactory.Create(),
        new WeaponCatalog(Options.Create(new WeaponOptions
        {
            Items = [new WeaponTemplateOptions { Code = "test", Name = "测试剑", MaxHp = 1 }],
            StarterPacks = new() { ["knight"] = ["test"] }
        })), ConsumableTestFactory.Create(), SoulImprintTestFactory.Create());
}
