using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>
/// Starts the next attempt against the same frozen room definition. The caller checks
/// permissions/outcome/deadlines and commits this tracked state with the room version.
/// Reward and participant history from previous attempts is retained.
/// </summary>
public sealed class DungeonRunLifecycleService(GameDbContext db, DungeonRunService runs,
    PartyScalingService scaling, MonsterCombatService? combat = null, DungeonRunRulesService? runRules = null)
{
    public async Task<Monster> BeginNextRunAsync(Room room, IReadOnlyList<BattleParticipant> party,
        DateTime startsAtUtc, bool automatic)
    {
        var statistics = new BattleStatisticsWriter(db);
        await statistics.FinishRunAsync(room, startsAtUtc);
        var monster = await runs.ResetEncounterAsync(room);
        foreach (var participant in party)
        {
            BattleConsumableBonusCalculator.Apply(participant.Character, null);
            participant.Character.Hp = TalentRules.EffectiveMaxHp(participant.Character);
        }
        foreach (var cooldown in await db.BattleConsumableCooldowns.Where(item => item.RoomId == room.Id).ToListAsync())
            cooldown.ReadyAtRound = 0;
        db.BattleOperationPotionStates.RemoveRange(await db.BattleOperationPotionStates
            .Where(item => item.RoomId == room.Id).ToListAsync());
        db.BattleConsumableBuffs.RemoveRange(await db.BattleConsumableBuffs
            .Where(item => item.RoomId == room.Id).ToListAsync());
        var skillCooldowns = await db.BattleSkillCooldowns.Where(item => item.RoomId == room.Id).ToListAsync();
        db.BattleSkillCooldowns.RemoveRange(skillCooldowns.Where(item => item.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix)));
        foreach (var cooldown in skillCooldowns.Where(item => !item.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix)))
            cooldown.ReadyAtRound = 0;

        ClearRoundState(room, party);
        foreach (var participant in party)
        {
            participant.Slot.HasParticipatedInRun = false;
            participant.Slot.LastParticipatedMonsterId = null;
        }
        room.RoundNumber = 0;
        room.RunSequence++;
        if (runRules is not null)
        {
            await runRules.RefreshAsync(room);
            monster = await db.Monsters.FindAsync(room.MonsterId)
                ?? throw new InvalidOperationException($"Room {room.Id} has no active monster after resetting its definition.");
        }
        room.Status = automatic ? RoomStatus.WaveTransition : RoomStatus.NotStarted;
        room.NextRoundAvailableAtUtc = automatic ? startsAtUtc : null;
        room.RoundCooldownDurationSeconds = automatic ? BattleRules.RepeatBattleDelaySeconds : null;
        room.PreparationStartedAtUtc = !automatic && room.IsPreparationTimeoutEnabled ? startsAtUtc : null;
        room.BattleEndedAtUtc = null;
        await scaling.SynchronizeAsync(room, party.Select(item => item.Slot).ToList());
        if (combat is not null) await combat.EnsureIntentAsync(room, monster);
        await statistics.RegisterRunAsync(room, startsAtUtc);
        return monster;
    }

    public static void ClearRoundState(Room room, IEnumerable<BattleParticipant> party)
    {
        room.PreparationStartedAtUtc = null;
        foreach (var participant in party)
        {
            var slot = participant.Slot;
            slot.IsConfirmed = false;
            slot.IsTemporaryAuto = false;
            slot.PendingConsumableSlotMask = 0;
            SkillQueueRules.Clear(slot);
            slot.IsSoulImprintQueued = false;
        }
    }
}
