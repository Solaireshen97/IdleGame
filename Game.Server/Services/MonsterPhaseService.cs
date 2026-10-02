using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Persistent, encounter-local phases. The caller owns the round transaction.</summary>
public sealed class MonsterPhaseService(GameDbContext db, MonsterCombatCatalog catalog,
    BattleStatusService statuses, DungeonRunRulesService? runRules = null)
{
    public FireCoreOptions? Definition(Room room, Monster monster) =>
        (runRules?.CombatFor(room) ?? catalog).FindProfile(monster.CombatProfileCode)?.FireCore;

    private async Task<BattleMonsterPhaseState?> FindAsync(Room room, Monster monster) =>
        db.BattleMonsterPhaseStates.Local.FirstOrDefault(state => db.Entry(state).State != EntityState.Deleted &&
            state.RoomId == room.Id && state.RunSequence == room.RunSequence && state.MonsterId == monster.Id)
        ?? await db.BattleMonsterPhaseStates.SingleOrDefaultAsync(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && state.MonsterId == monster.Id);

    public async Task BeginRoundAsync(Room room, Monster monster, List<string> logs)
    {
        if (runRules is not null) await runRules.EnsureAsync(room);
        if (Definition(room, monster) is not { } core || monster.Hp <= 0) return;
        var state = await FindAsync(room, monster);
        if (state is null)
        {
            state = new BattleMonsterPhaseState
            {
                RoomId = room.Id, RunSequence = room.RunSequence, MonsterId = monster.Id,
                EncounterStartRound = room.RoundNumber, NextActivationRound = core.FirstActivationRound
            };
            db.BattleMonsterPhaseStates.Add(state);
        }
        if (state.LastPreparedRound == room.RoundNumber) return;
        state.LastPreparedRound = room.RoundNumber;
        if (state.RewardStartsAtRound == room.RoundNumber)
        {
            // Rounds statuses include the application round and then durationRounds more rounds.
            await statuses.ApplyAsync(room, "Monster", monster.Id, core.RewardStatusCode, core.RewardRounds - 1,
                logs, monster.Name, source: new("Monster", monster.Id, "fire-core-break"));
            state.RewardStartsAtRound = null;
        }
        var localRound = room.RoundNumber - state.EncounterStartRound + 1;
        if (!ShouldActivate(core, state, monster, localRound)) return;
        // Single HP-triggered phases never recur. Periodic phases keep their original cadence after a break.
        if (core.TriggerHpPercent is null)
            while (state.NextActivationRound <= localRound) state.NextActivationRound += core.CycleRounds;
        state.IsActive = true;
        state.WaterDamage = 0;
        state.ExpiresAfterRound = checked(room.RoundNumber + core.WindowRounds - 1);
        state.LastActivationRound = room.RoundNumber;
        state.ActivationCount++;
        await statuses.ApplyAsync(room, "Monster", monster.Id, core.HeatingStatusCode, core.WindowRounds - 1,
            logs, monster.Name, source: new("Monster", monster.Id, "fire-core-activation"));
        logs.Add($"{monster.Name} 开始蓄热：{core.WindowRounds}回合内累计造成 {RequiredDamage(monster, core)} 点水属性直接伤害可破核。");
    }

    public static long RequiredDamage(Monster monster, FireCoreOptions core) =>
        (long)decimal.Ceiling(monster.MaxHp * core.BreakWaterDamagePercent / 100m);

    public async Task ObserveDirectDamageAsync(BattleExecutionContext battle, ElementType? element, int actualDamage)
    {
        if (element != ElementType.Water || actualDamage <= 0 || Definition(battle.Room, battle.Monster) is not { } core) return;
        var state = await FindAsync(battle.Room, battle.Monster);
        if (state is null || !state.IsActive || state.ExpiresAfterRound < battle.Room.RoundNumber) return;
        state.WaterDamage += actualDamage;
        if (state.WaterDamage < RequiredDamage(battle.Monster, core)) return;
        state.IsActive = false;
        state.BreakCount++;
        state.LastBreakRound = battle.Room.RoundNumber;
        state.RewardStartsAtRound = checked(battle.Room.RoundNumber + 1);
        await statuses.RemoveAsync(battle.Room, "Monster", battle.Monster.Id, core.HeatingStatusCode);
        var removed = core.ExtraTargetCount > 0 ? "蓄热与扩散" : "蓄热";
        battle.Logs.Add($"{battle.Monster.Name} 的熔岩核心被击破，{removed}立即解除；接下来完整{core.RewardRounds}回合获得破核增伤窗口。");
    }

    public async Task<bool> IsHeatedAsync(Room room, Monster monster) =>
        Definition(room, monster) is not null && await FindAsync(room, monster) is { IsActive: true } state &&
        state.ExpiresAfterRound >= room.RoundNumber;

    public async Task<bool> WillBeHeatedAsync(Room room, Monster monster)
    {
        if (monster.Hp <= 0 || Definition(room, monster) is not { } core || await FindAsync(room, monster) is not { } state) return false;
        return state.IsActive && state.ExpiresAfterRound >= room.RoundNumber ||
            ShouldActivate(core, state, monster, room.RoundNumber - state.EncounterStartRound + 1);
    }

    private static bool ShouldActivate(FireCoreOptions core, BattleMonsterPhaseState state, Monster monster, int localRound) =>
        core.TriggerHpPercent is { } hp
            ? state.ActivationCount == 0 && (long)monster.Hp * 100 <= (long)monster.MaxHp * hp
            : localRound >= state.NextActivationRound;

    public async Task RecordLinkedHitAsync(Room room, Monster monster)
    {
        if (await FindAsync(room, monster) is { } state) state.LinkedHitCount++;
    }

    public async Task EndRoundAsync(Room room, Monster monster, List<string> logs)
    {
        if (Definition(room, monster) is not { } core || await FindAsync(room, monster) is not { IsActive: true } state) return;
        if (monster.Hp <= 0) { state.IsActive = false; return; }
        if (room.RoundNumber < state.ExpiresAfterRound) return;
        state.IsActive = false;
        state.ExpiryCount++;
        state.LastExpiryRound = room.RoundNumber;
        await statuses.RemoveAsync(room, "Monster", monster.Id, core.HeatingStatusCode);
        logs.Add($"{monster.Name} 的蓄热结束，恢复常态。");
    }

    public async Task ResetRoomAsync(int roomId)
    {
        var states = await db.BattleMonsterPhaseStates.Where(state => state.RoomId == roomId).ToListAsync();
        db.BattleMonsterPhaseStates.RemoveRange(states.Concat(db.BattleMonsterPhaseStates.Local
            .Where(state => state.RoomId == roomId)).Distinct().ToList());
    }
}
