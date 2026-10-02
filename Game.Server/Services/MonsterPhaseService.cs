using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Persistent, encounter-local phases. The caller owns the round transaction.</summary>
public sealed partial class MonsterPhaseService(GameDbContext db, MonsterCombatCatalog catalog,
    BattleStatusService statuses, DungeonRunRulesService? runRules = null)
{
    public FireCoreOptions? Definition(Room room, Monster monster) =>
        (runRules?.CombatFor(room) ?? catalog).FindProfile(monster.CombatProfileCode)?.FireCore;

    private async Task<BattleMonsterPhaseState?> FindAsync(Room room, Monster monster) =>
        db.BattleMonsterPhaseStates.Local.FirstOrDefault(state => db.Entry(state).State != EntityState.Deleted &&
            state.RoomId == room.Id && state.RunSequence == room.RunSequence && state.MonsterId == monster.Id)
        ?? await db.BattleMonsterPhaseStates.SingleOrDefaultAsync(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && state.MonsterId == monster.Id);

    // Zero-based skill clock. Reuse the persisted encounter state across intent previews/reloads.
    public async Task<int> EncounterSkillRoundAsync(Room room, Monster monster)
    {
        var state = await FindAsync(room, monster);
        if (state is null)
        {
            state = new BattleMonsterPhaseState
            {
                RoomId = room.Id, RunSequence = room.RunSequence, MonsterId = monster.Id,
                EncounterStartRound = room.RoundNumber,
                NextActivationRound = Definition(room, monster)?.FirstActivationRound ??
                    DeepColdDefinition(room, monster)?.FirstActivationRound ??
                    EarthArmorDefinition(room, monster)?.FirstActivationRound ??
                    StaticFieldDefinition(room, monster)?.FirstActivationRound ??
                    ReflectionMirrorDefinition(room, monster)?.FirstActivationRound ??
                    PlaguePoisonDefinition(room, monster)?.FirstActivationRound ?? 0
            };
            db.BattleMonsterPhaseStates.Add(state);
        }
        return room.RoundNumber - state.EncounterStartRound;
    }

    public async Task BeginRoundAsync(Room room, Monster monster, List<string> logs,
        IReadOnlyList<BattleParticipant>? participants = null)
    {
        if (runRules is not null) await runRules.EnsureAsync(room);
        if (PlaguePoisonDefinition(room, monster) is { } poison)
        {
            await BeginPlagueRoundAsync(room, monster, poison, logs, participants);
            return;
        }
        if (ReflectionMirrorDefinition(room, monster) is { } mirror)
        {
            await BeginReflectionMirrorRoundAsync(room, monster, mirror, logs, participants);
            return;
        }
        if (StaticFieldDefinition(room, monster) is { } field)
        {
            await BeginStaticFieldRoundAsync(room, monster, field, logs);
            return;
        }
        if (EarthArmorDefinition(room, monster) is { } armor)
        {
            await BeginEarthArmorRoundAsync(room, monster, armor, logs);
            return;
        }
        if (DeepColdDefinition(room, monster) is { } cold)
        {
            await BeginDeepColdRoundAsync(room, monster, cold, logs, participants);
            return;
        }
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

    public async Task ObserveDirectDamageAsync(BattleExecutionContext battle, ElementType? element, int actualDamage,
        int? sourceCharacterId = null)
    {
        if (PlaguePoisonDefinition(battle.Room, battle.Monster) is { } poison)
        {
            await ObservePlagueDamageAsync(battle, poison, element, actualDamage);
            return;
        }
        if (ReflectionMirrorDefinition(battle.Room, battle.Monster) is { } mirror)
        {
            await ObserveReflectionMirrorDamageAsync(battle, mirror, element, actualDamage, sourceCharacterId);
            return;
        }
        if (StaticFieldDefinition(battle.Room, battle.Monster) is { } field)
        {
            await ObserveStaticFieldDamageAsync(battle, field, element, actualDamage, sourceCharacterId);
            return;
        }
        if (EarthArmorDefinition(battle.Room, battle.Monster) is { } armor)
        {
            await ObserveEarthArmorDamageAsync(battle, armor, element, actualDamage);
            return;
        }
        if (sourceCharacterId.HasValue && element.HasValue && actualDamage > 0)
            await MeltDeepColdAsync(battle, sourceCharacterId.Value, element.Value);
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

    public async Task EndRoundAsync(Room room, Monster monster, List<string> logs,
        IReadOnlyList<BattleParticipant>? participants = null,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses = null)
    {
        if (PlaguePoisonDefinition(room, monster) is { } poison)
        {
            await EndPlagueRoundAsync(room, monster, poison, logs, participants, operationBonuses);
            return;
        }
        if (ReflectionMirrorDefinition(room, monster) is { } mirror)
        {
            await EndReflectionMirrorRoundAsync(room, monster, mirror, logs);
            return;
        }
        if (StaticFieldDefinition(room, monster) is { } field)
        {
            await EndStaticFieldRoundAsync(room, monster, field, logs);
            return;
        }
        if (EarthArmorDefinition(room, monster) is { } armor)
        {
            await EndEarthArmorRoundAsync(room, monster, armor, logs);
            return;
        }
        if (DeepColdDefinition(room, monster) is { } cold)
        {
            await EndDeepColdRoundAsync(room, monster, cold, logs);
            return;
        }
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
