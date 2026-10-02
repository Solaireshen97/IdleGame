using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Routes encounter events to the configured phase. The caller owns the round transaction.</summary>
public sealed partial class MonsterPhaseService(GameDbContext db, MonsterCombatCatalog catalog,
    BattleStatusService statuses, DungeonRunRulesService? runRules = null)
{
    private MonsterCombatProfileOptions? Profile(Room room, Monster monster) =>
        (runRules?.CombatFor(room) ?? catalog).FindProfile(monster.CombatProfileCode);

    private async Task PrepareDefinitionsAsync(Room room)
    {
        if (runRules is not null) await runRules.EnsureAsync(room);
    }

    private async Task<BattleMonsterPhaseState?> FindAsync(Room room, Monster monster) =>
        db.BattleMonsterPhaseStates.Local.FirstOrDefault(state => db.Entry(state).State != EntityState.Deleted &&
            state.RoomId == room.Id && state.RunSequence == room.RunSequence && state.MonsterId == monster.Id)
        ?? await db.BattleMonsterPhaseStates.SingleOrDefaultAsync(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && state.MonsterId == monster.Id);

    private async Task<BattleMonsterPhaseState> EnsureStateAsync(Room room, Monster monster)
    {
        await PrepareDefinitionsAsync(room);
        if (await FindAsync(room, monster) is { } existing) return existing;
        var state = new BattleMonsterPhaseState
        {
            RoomId = room.Id, RunSequence = room.RunSequence, MonsterId = monster.Id,
            EncounterStartRound = room.RoundNumber,
            NextActivationRound = Profile(room, monster)?.Phase?.FirstActivationRound ?? 0
        };
        db.BattleMonsterPhaseStates.Add(state);
        return state;
    }

    // Zero-based skill clock, shared with phases and persisted across previews/reloads.
    public async Task<int> EncounterSkillRoundAsync(Room room, Monster monster) =>
        room.RoundNumber - (await EnsureStateAsync(room, monster)).EncounterStartRound;

    public async Task BeginRoundAsync(Room room, Monster monster, List<string> logs,
        IReadOnlyList<BattleParticipant>? participants = null)
    {
        await PrepareDefinitionsAsync(room);
        await (Profile(room, monster)?.Phase switch
        {
            FireCoreOptions core => BeginFireCoreRoundAsync(room, monster, core, logs),
            DeepColdOptions cold => BeginDeepColdRoundAsync(room, monster, cold, logs, participants),
            EarthArmorOptions armor => BeginEarthArmorRoundAsync(room, monster, armor, logs),
            StaticFieldOptions field => BeginStaticFieldRoundAsync(room, monster, field, logs),
            ReflectionMirrorOptions mirror => BeginReflectionMirrorRoundAsync(room, monster, mirror, logs, participants),
            PlaguePoisonOptions poison => BeginPlagueRoundAsync(room, monster, poison, logs, participants),
            _ => Task.CompletedTask
        });
    }

    public async Task ObserveDirectDamageAsync(BattleExecutionContext battle, ElementType? element, int actualDamage,
        int? sourceCharacterId = null)
    {
        await PrepareDefinitionsAsync(battle.Room);
        await (Profile(battle.Room, battle.Monster)?.Phase switch
        {
            FireCoreOptions core => ObserveFireCoreDamageAsync(battle, core, element, actualDamage),
            DeepColdOptions when sourceCharacterId.HasValue && element.HasValue && actualDamage > 0 =>
                MeltDeepColdAsync(battle, sourceCharacterId.Value, element.Value),
            EarthArmorOptions armor => ObserveEarthArmorDamageAsync(battle, armor, element, actualDamage),
            StaticFieldOptions field => ObserveStaticFieldDamageAsync(battle, field, element, actualDamage, sourceCharacterId),
            ReflectionMirrorOptions mirror => ObserveReflectionMirrorDamageAsync(battle, mirror, element, actualDamage, sourceCharacterId),
            PlaguePoisonOptions poison => ObservePlagueDamageAsync(battle, poison, element, actualDamage),
            _ => Task.CompletedTask
        });
    }

    public async Task ObserveCleanseAsync(BattleExecutionContext battle, int characterId, string code)
    {
        await PrepareDefinitionsAsync(battle.Room);
        await (Profile(battle.Room, battle.Monster)?.Phase switch
        {
            DeepColdOptions => ObserveDeepColdCleanseAsync(battle, characterId, code),
            PlaguePoisonOptions => ObservePlagueCleanseAsync(battle, characterId, code),
            _ => Task.CompletedTask
        });
    }

    public async Task ObserveDispelAsync(BattleExecutionContext battle, string code)
    {
        await PrepareDefinitionsAsync(battle.Room);
        if (Profile(battle.Room, battle.Monster)?.Phase is ReflectionMirrorOptions)
            await ObserveReflectionMirrorDispelAsync(battle, code);
    }

    public async Task<bool> SuppressMonsterStatusAsync(Room room, Monster monster, int characterId, string code)
    {
        await PrepareDefinitionsAsync(room);
        return await (Profile(room, monster)?.Phase switch
        {
            DeepColdOptions => SuppressBasicColdAsync(room, monster, characterId, code),
            PlaguePoisonOptions => SuppressBasicPoisonAsync(room, monster, characterId, code),
            _ => Task.FromResult(false)
        });
    }

    public async Task EndRoundAsync(Room room, Monster monster, List<string> logs,
        IReadOnlyList<BattleParticipant>? participants = null,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses = null)
    {
        await PrepareDefinitionsAsync(room);
        await (Profile(room, monster)?.Phase switch
        {
            FireCoreOptions core => EndFireCoreRoundAsync(room, monster, core, logs),
            DeepColdOptions cold => EndDeepColdRoundAsync(room, monster, cold, logs),
            EarthArmorOptions armor => EndEarthArmorRoundAsync(room, monster, armor, logs),
            StaticFieldOptions field => EndStaticFieldRoundAsync(room, monster, field, logs),
            ReflectionMirrorOptions mirror => EndReflectionMirrorRoundAsync(room, monster, mirror, logs),
            PlaguePoisonOptions poison => EndPlagueRoundAsync(room, monster, poison, logs, participants, operationBonuses),
            _ => Task.CompletedTask
        });
    }

    public async Task ResetRoomAsync(int roomId)
    {
        var states = await db.BattleMonsterPhaseStates.Where(state => state.RoomId == roomId).ToListAsync();
        db.BattleMonsterPhaseStates.RemoveRange(states.Concat(db.BattleMonsterPhaseStates.Local
            .Where(state => state.RoomId == roomId)).Distinct().ToList());
    }
}
