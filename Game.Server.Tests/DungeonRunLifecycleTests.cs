using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextAttemptUsesOneCleanupBoundaryAndRetainsFrozenRulesAndPreviousHistory(bool automatic)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var room = test.Room;
        room.RunSequence = 3;
        room.RoundNumber = 7;
        room.Status = RoomStatus.BattleOver;
        room.BattleEndedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        room.CurrentWaveNumber = 2;
        room.TotalWaveCount = 2;
        test.Monster.RoomId = room.Id;
        test.Monster.WaveNumber = test.Monster.Position = 1;
        var final = new Monster { RoomId = room.Id, WaveNumber = 2, Position = 1,
            Name = "Final", Hp = 0, MaxHp = 60, BaseMaxHp = 60, Attack = 3 };
        test.Db.Monsters.Add(final);
        await test.Db.SaveChangesAsync();
        room.MonsterId = final.Id;
        var definition = new DungeonRunDefinition { DungeonCode = "slime-field", DungeonKind = "Hunt", DepthLevel = room.DepthLevel,
            RewardEligibility = DungeonRewardEligibility.CurrentSlots, PartyHpPercentages = [100, 100, 100, 100, 100],
            Monsters = [DungeonMonsterDefinition.Capture(test.Monster), DungeonMonsterDefinition.Capture(final)] };
        var frozen = await PersistLifecycleDefinitionAsync(test, definition);
        // Runtime changes must be restored from the room's definition, not carried into the next attempt.
        test.Monster.Hp = 0;
        test.Monster.Attack = 999;
        test.Monster.BaseMaxHp = 999;
        test.Character.Hp = 0;
        test.Character.TemporaryWeaponHealthBonusPercent = 50;
        var slot = await test.Db.RoomSlots.SingleAsync();
        slot.IsConfirmed = slot.IsTemporaryAuto = slot.HasParticipatedInRun = slot.IsSoulImprintQueued = true;
        slot.IsAutoEnabled = true;
        slot.PendingConsumableSlotMask = slot.PendingSkillSlotMask = 1;
        slot.PendingSkillTargetsJson = "{}";
        slot.LastParticipatedMonsterId = final.Id;
        test.Db.AddRange(
            new RewardRun { RoomId = room.Id, Sequence = 3, Status = "Victory", SettledAtUtc = DateTime.UtcNow },
            new RewardEntry { RoomId = room.Id, Sequence = 3, CharacterId = test.Character.Id, UserId = 1,
                EventKey = "clear", Kind = "Gold", Quantity = 13 },
            new DungeonRunParticipant { RoomId = room.Id, RunSequence = 3, CharacterId = test.Character.Id, MasteryLevel = 4 },
            new BattleHealingPotionState { RoomId = room.Id, RunSequence = 3, CharacterId = test.Character.Id, UsesUsed = 2 },
            new BattleOperationPotionState { RoomId = room.Id, RunSequence = 3, CharacterId = test.Character.Id, AttackPercent = 10 },
            new BattleConsumableBuff { RoomId = room.Id, RunSequence = 3, CharacterId = test.Character.Id,
                WeaponSkillCode = "weapon-critical", SkillLevel = 1, ExpiresAfterRound = 10 },
            new BattleConsumableCooldown { RoomId = room.Id, CharacterId = test.Character.Id, CooldownGroup = "healing", ReadyAtRound = 9 },
            new BattleSkillCooldown { RoomId = room.Id, CharacterId = test.Character.Id, SkillCode = "knight-strike", ReadyAtRound = 9 },
            new BattleSkillCooldown { RoomId = room.Id, CharacterId = test.Character.Id,
                SkillCode = SoulImprintRules.CooldownCode("test"), ReadyAtRound = 9 });
        await test.Db.SaveChangesAsync();
        var rewards = RewardTestFactory.CreateService(test.Db, ProgressionTestFactory.Create());
        var rules = LifecycleRules(test);
        var lifecycle = new DungeonRunLifecycleService(test.Db, new(test.Db, rewards),
            new(test.Db, PartyScalingCatalog.Default, rules), runRules: rules);
        var startsAt = DateTime.UtcNow;

        var active = await lifecycle.BeginNextRunAsync(room, [new(slot, test.Character)], startsAt, automatic);

        Assert.Equal(4, room.RunSequence);
        Assert.Equal(0, room.RoundNumber);
        Assert.Equal(test.Monster.Id, active.Id);
        Assert.Equal(12, active.Attack);
        Assert.Equal(50, active.MaxHp);
        Assert.Equal(active.MaxHp, active.Hp);
        Assert.Equal(1, room.CurrentWaveNumber);
        Assert.Equal(2, room.TotalWaveCount);
        Assert.Equal(100, test.Character.Hp);
        Assert.Equal(0m, test.Character.TemporaryWeaponHealthBonusPercent);
        Assert.False(slot.IsConfirmed || slot.IsTemporaryAuto || slot.HasParticipatedInRun || slot.IsSoulImprintQueued);
        Assert.True(slot.IsAutoEnabled);
        Assert.Equal(0, slot.PendingConsumableSlotMask | slot.PendingSkillSlotMask);
        Assert.Null(slot.PendingSkillTargetsJson);
        Assert.Null(slot.LastParticipatedMonsterId);
        Assert.Null(room.BattleEndedAtUtc);
        Assert.Equal(automatic ? RoomStatus.WaveTransition : RoomStatus.NotStarted, room.Status);
        Assert.Equal(automatic ? startsAt : (DateTime?)null, room.NextRoundAvailableAtUtc);
        Assert.Equal(automatic ? BattleRules.RepeatBattleDelaySeconds : (int?)null, room.RoundCooldownDurationSeconds);
        Assert.Equal(automatic ? (DateTime?)null : startsAt, room.PreparationStartedAtUtc);
        await using (var unchanged = test.CreateDbContext())
            Assert.Equal(3, (await unchanged.Rooms.SingleAsync()).RunSequence); // The caller owns the commit.
        await test.Db.SaveChangesAsync();
        Assert.Equal(frozen.Revision, (await test.Db.DungeonRunRuleSnapshots.SingleAsync()).Revision);
        Assert.Empty(await test.Db.BattleOperationPotionStates.ToListAsync());
        Assert.Empty(await test.Db.BattleConsumableBuffs.ToListAsync());
        Assert.Equal(0, (await test.Db.BattleConsumableCooldowns.SingleAsync()).ReadyAtRound);
        Assert.Equal(0, (await test.Db.BattleSkillCooldowns.SingleAsync()).ReadyAtRound);
        Assert.Equal("Victory", (await test.Db.RewardRuns.SingleAsync()).Status);
        Assert.Equal(13, (await test.Db.RewardEntries.SingleAsync()).Quantity);
        Assert.Equal(3, (await test.Db.DungeonRunParticipants.SingleAsync()).RunSequence);
        Assert.Equal(2, (await test.Db.BattleHealingPotionStates.SingleAsync()).UsesUsed);
    }

    [Theory]
    [InlineData(DungeonRewardEligibility.CurrentSlots, false)]
    [InlineData(DungeonRewardEligibility.CurrentSlots, true)]
    [InlineData(DungeonRewardEligibility.ActualParticipants, false)]
    [InlineData(DungeonRewardEligibility.ActualParticipants, true)]
    public async Task FrozenRewardEligibilityIsIndependentOfDepthDefinition(DungeonRewardEligibility eligibility, bool hasDepth)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var other = await test.AddSlotAsync(2, "Other");
        var depth = hasDepth ? new DungeonDepthDefinitionOptions() : null;
        test.Room.DepthDefinitionJson = depth is null ? null : JsonSerializer.Serialize(depth);
        test.Monster.Hp = 0;
        await PersistLifecycleDefinitionAsync(test, new()
        {
            DungeonCode = "slime-field", DungeonKind = "Hunt", DepthLevel = test.Room.DepthLevel, Depth = depth,
            RewardEligibility = eligibility, PartyHpPercentages = [100, 100, 100, 100, 100],
            Monsters = [DungeonMonsterDefinition.Capture(test.Monster)]
        });
        var rewards = RewardTestFactory.CreateService(test.Db, ProgressionTestFactory.Create());
        var runs = new DungeonRunService(test.Db, rewards, runRules: LifecycleRules(test));

        var result = await runs.AdvanceAfterDefeatAsync(test.Room, test.Monster,
            [new(1, test.Character), new(1, other)], DateTime.UtcNow, [], [test.Character.Id], [test.Character.Id]);
        await test.Db.SaveChangesAsync();

        Assert.Null(result.Error);
        Assert.True(result.IsDungeonComplete);
        Assert.Equal(13, test.Character.Gold);
        Assert.Equal(eligibility == DungeonRewardEligibility.CurrentSlots ? 13 : 0, other.Gold);
        Assert.Equal("Victory", (await test.Db.RewardRuns.SingleAsync()).Status);
        Assert.False(await test.Db.CharacterBattleMilestones.AnyAsync(item => item.CharacterId == other.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualParticipationPolicyRejectsMissingEvidenceBeforeAwarding(bool missingMonsterEvidence)
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Monster.Hp = 0;
        await PersistLifecycleDefinitionAsync(test, new()
        {
            DungeonCode = "slime-field", DungeonKind = "Hunt", DepthLevel = test.Room.DepthLevel,
            RewardEligibility = DungeonRewardEligibility.ActualParticipants,
            PartyHpPercentages = [100, 100, 100, 100, 100],
            Monsters = [DungeonMonsterDefinition.Capture(test.Monster)]
        });
        var rewards = RewardTestFactory.CreateService(test.Db, ProgressionTestFactory.Create());
        var runs = new DungeonRunService(test.Db, rewards, runRules: LifecycleRules(test));
        var actualIds = new[] { test.Character.Id };
        // The fixture already grants a clear and an Auto-unlock milestone. Rejection must preserve them.
        var previousClears = JsonSerializer.Serialize(await test.Db.UserDungeonClears.AsNoTracking().ToListAsync());
        var previousMilestones = JsonSerializer.Serialize(await test.Db.CharacterBattleMilestones.AsNoTracking().ToListAsync());

        var result = await runs.AdvanceAfterDefeatAsync(test.Room, test.Monster,
            [new(1, test.Character)], DateTime.UtcNow, [],
            missingMonsterEvidence ? null : actualIds, missingMonsterEvidence ? actualIds : null);
        await test.Db.SaveChangesAsync();

        Assert.Equal("MissingParticipationEvidence", result.Error);
        Assert.False(result.IsDungeonComplete);
        Assert.Equal(0, test.Character.Gold);
        Assert.Empty(await test.Db.RewardRuns.ToListAsync());
        Assert.Empty(await test.Db.RewardEntries.ToListAsync());
        Assert.Empty(await test.Db.RewardEvents.ToListAsync());
        Assert.Equal(previousClears, JsonSerializer.Serialize(await test.Db.UserDungeonClears.AsNoTracking().ToListAsync()));
        Assert.Equal(previousMilestones, JsonSerializer.Serialize(await test.Db.CharacterBattleMilestones.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public void RoundCleanupPreservesAttemptParticipationAndPermanentAutoChoice()
    {
        var slot = new RoomSlot { IsConfirmed = true, IsTemporaryAuto = true, IsAutoEnabled = true,
            PendingSkillSlotMask = 1, PendingConsumableSlotMask = 1, PendingSkillTargetsJson = "{}",
            IsSoulImprintQueued = true, HasParticipatedInRun = true, LastParticipatedMonsterId = 42 };
        var room = new Room { RoundNumber = 7, RunSequence = 3, PreparationStartedAtUtc = DateTime.UtcNow };
        DungeonRunLifecycleService.ClearRoundState(room, [new(slot, new())]);
        Assert.Null(room.PreparationStartedAtUtc);
        Assert.False(slot.IsConfirmed || slot.IsTemporaryAuto || slot.IsSoulImprintQueued);
        Assert.Equal(0, slot.PendingConsumableSlotMask | slot.PendingSkillSlotMask);
        Assert.Null(slot.PendingSkillTargetsJson);
        Assert.True(slot.IsAutoEnabled && slot.HasParticipatedInRun);
        Assert.Equal(42, slot.LastParticipatedMonsterId);
        Assert.Equal((7, 3), (room.RoundNumber, room.RunSequence));
    }

    private static DungeonRunRulesService LifecycleRules(BattleTestContext test) => new(test.Db,
        new(Options.Create(new MonsterCombatOptions())), RewardTestFactory.CreateCatalog(), PartyScalingCatalog.Default,
        new(Options.Create(new DungeonDepthOptions())));

    private static async Task<DungeonRunRuleSnapshot> PersistLifecycleDefinitionAsync(BattleTestContext test,
        DungeonRunDefinition definition)
    {
        var json = JsonSerializer.Serialize(definition);
        var row = new DungeonRunRuleSnapshot { RoomId = test.Room.Id, DefinitionJson = json,
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant() };
        test.Db.DungeonRunRuleSnapshots.Add(row);
        await test.Db.SaveChangesAsync();
        return row;
    }
}
