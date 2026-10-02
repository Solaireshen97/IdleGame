using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed partial class MonsterPhaseService
{
    public ReflectionMirrorOptions? ReflectionMirrorDefinition(Room room, Monster monster) =>
        (runRules?.CombatFor(room) ?? catalog).FindProfile(monster.CombatProfileCode)?.ReflectionMirror;

    private async Task BeginReflectionMirrorRoundAsync(Room room, Monster monster, ReflectionMirrorOptions mirror,
        List<string> logs, IReadOnlyList<BattleParticipant>? participants)
    {
        if (monster.Hp <= 0) return;
        await EncounterSkillRoundAsync(room, monster);
        var state = (await FindAsync(room, monster))!;
        if (state.LastPreparedRound == room.RoundNumber) return;
        state.LastPreparedRound = room.RoundNumber;
        if (state.RewardStartsAtRound == room.RoundNumber)
        {
            await statuses.ApplyAsync(room, "Monster", monster.Id, mirror.RewardStatusCode, mirror.RewardRounds - 1,
                logs, monster.Name, source: new("Monster", monster.Id, "reflection-mirror-break"));
            state.RewardStartsAtRound = null;
        }
        var localRound = room.RoundNumber - state.EncounterStartRound + 1;
        var shouldActivate = mirror.TriggerHpPercent is { } hp
            ? state.ActivationCount == 0 && (long)monster.Hp * 100 <= (long)monster.MaxHp * hp
            : localRound >= state.NextActivationRound;
        if (shouldActivate)
        {
            // A break or natural end never moves the next fixed activation.
            if (mirror.TriggerHpPercent is null)
                while (state.NextActivationRound <= localRound) state.NextActivationRound += mirror.CycleRounds;
            await statuses.RemoveAsync(room, "Monster", monster.Id, mirror.MirrorStatusCode);
            await RemoveMirrorAmplificationAsync(room, monster, mirror);
            state.IsActive = true;
            state.ActivationCount++;
            state.LastActivationRound = room.RoundNumber;
            state.ExpiresAfterRound = checked(room.RoundNumber + mirror.WindowRounds - 1);
            await statuses.ApplyAsync(room, "Monster", monster.Id, mirror.MirrorStatusCode, mirror.WindowRounds - 1,
                logs, monster.Name, source: new("Monster", monster.Id, "reflection-mirror-activation"),
                boundTargetType: "Monster", boundTargetId: monster.Id, counterCount: mirror.InitialStacks);
            logs.Add($"{monster.Name} 开启琉辉反镜：强度{mirror.InitialStacks}，持续{mirror.WindowRounds}回合。驱散直接解除；每名角色每回合首次有效暗属性直接伤害削弱1档。");
        }
        if (!state.IsActive || state.ExpiresAfterRound < room.RoundNumber) return;
        foreach (var p in participants ?? [])
            if (p.Character.Hp > 0) await MirrorBudgetAsync(room, monster, mirror, p.Character.Id,
                CharacterCombatStatSnapshot.Capture(p.Character).MaxHp);
    }

    private async Task<BattleStatusEffect> MirrorBudgetAsync(Room room, Monster monster, ReflectionMirrorOptions mirror,
        int characterId, int maxHp)
    {
        var budget = (await statuses.GetActiveAsync(room, "Character", [characterId]))
            .SingleOrDefault(s => s.EffectCode == mirror.BudgetStatusCode && s.BoundTargetId == monster.Id);
        if (budget is not null) return budget;
        await statuses.ApplyAsync(room, "Character", characterId, mirror.BudgetStatusCode, 0, [], "",
            perTickValue: maxHp, boundTargetType: "Monster", boundTargetId: monster.Id, magnitudeSnapshot: 0);
        return (await statuses.GetActiveAsync(room, "Character", [characterId]))
            .Single(s => s.EffectCode == mirror.BudgetStatusCode);
    }

    private async Task ObserveReflectionMirrorDamageAsync(BattleExecutionContext battle, ReflectionMirrorOptions mirror,
        ElementType? element, int damage, int? characterId)
    {
        if (damage <= 0 || element != mirror.RemovalElement || characterId is not { } id || battle.Monster.Hp <= 0 ||
            battle.Party.All(p => p.Character.Id != id || p.Character.Hp <= 0) ||
            await FindAsync(battle.Room, battle.Monster) is not { IsActive: true } state ||
            state.ExpiresAfterRound < battle.Room.RoundNumber ||
            await statuses.HasAsync(battle.Room, "Character", id, mirror.HitUsedStatusCode)) return;
        if (await statuses.ConsumeAsync(battle.Room, "Monster", battle.Monster.Id, mirror.MirrorStatusCode, 1) == 0) return;
        await statuses.ApplyAsync(battle.Room, "Character", id, mirror.HitUsedStatusCode, 0, [], "",
            boundTargetType: "Monster", boundTargetId: battle.Monster.Id);
        state.LinkedHitCount++;
        var strength = await ReflectionMirrorStacksAsync(battle.Room, battle.Monster);
        battle.Logs.Add($"暗属性攻击削弱琉辉反镜，剩余强度{strength}。");
        if (strength == 0) await BreakReflectionMirrorAsync(battle, mirror, state);
    }

    public async Task ObserveReflectionMirrorDispelAsync(BattleExecutionContext battle, string code)
    {
        if (ReflectionMirrorDefinition(battle.Room, battle.Monster) is not { } mirror || code != mirror.MirrorStatusCode ||
            battle.Monster.Hp <= 0 || await FindAsync(battle.Room, battle.Monster) is not { IsActive: true } state ||
            state.ExpiresAfterRound < battle.Room.RoundNumber) return;
        // Only the actual dispel notification succeeds. Expiry/death/removing another buff never does.
        await BreakReflectionMirrorAsync(battle, mirror, state);
    }

    private async Task BreakReflectionMirrorAsync(BattleExecutionContext battle, ReflectionMirrorOptions mirror,
        BattleMonsterPhaseState state)
    {
        state.IsActive = false;
        state.BreakCount++;
        state.LastBreakRound = battle.Room.RoundNumber;
        state.RewardStartsAtRound = checked(battle.Room.RoundNumber + 1);
        await statuses.RemoveAsync(battle.Room, "Monster", battle.Monster.Id, mirror.MirrorStatusCode);
        await RemoveMirrorAmplificationAsync(battle.Room, battle.Monster, mirror);
        battle.Logs.Add($"{battle.Monster.Name} 的琉辉反镜被主动解除；下一完整回合起获得{mirror.RewardRounds}回合棱核失衡反攻窗口。");
    }

    public async Task<int> ReflectionMirrorStacksAsync(Room room, Monster monster) =>
        ReflectionMirrorDefinition(room, monster) is { } mirror && monster.Hp > 0 &&
        await FindAsync(room, monster) is { IsActive: true } state && state.ExpiresAfterRound >= room.RoundNumber
            ? await statuses.StacksAsync(room, "Monster", monster.Id, mirror.MirrorStatusCode) : 0;

    public async Task<(decimal ReflectPercentPerStack, decimal CapPercentPerStack)> ReflectionMirrorRatesAsync(Room room, Monster monster)
    {
        if (ReflectionMirrorDefinition(room, monster) is not { } mirror) return (0, 0);
        var level = string.IsNullOrEmpty(mirror.AmplificationStatusCode) ? 0 :
            await statuses.StacksAsync(room, "Monster", monster.Id, mirror.AmplificationStatusCode);
        return (mirror.ReflectPercentPerStack + level * mirror.ReflectGrowthPercentPerStack,
            mirror.MaxHpCapPercentPerStack + level * mirror.MaxHpCapGrowthPercentPerStack);
    }

    public async Task<decimal> ReflectionMirrorRawAsync(BattleExecutionContext battle, int damage) => damage > 0
        ? damage * (await ReflectionMirrorRatesAsync(battle.Room, battle.Monster)).ReflectPercentPerStack *
          await ReflectionMirrorStacksAsync(battle.Room, battle.Monster) / 100m : 0;

    public async Task<int> ConsumeReflectionMirrorBudgetAsync(BattleExecutionContext battle, BattleActor actor, decimal raw)
    {
        if (raw <= 0 || actor.Hp <= 0 || ReflectionMirrorDefinition(battle.Room, battle.Monster) is not { } mirror) return 0;
        var strength = await ReflectionMirrorStacksAsync(battle.Room, battle.Monster);
        if (strength == 0) return 0;
        var budget = await MirrorBudgetAsync(battle.Room, battle.Monster, mirror, actor.Id, actor.MaxHp);
        var cap = decimal.Floor(budget.PerTickValue!.Value *
            (await ReflectionMirrorRatesAsync(battle.Room, battle.Monster)).CapPercentPerStack * strength / 100m);
        var used = budget.MagnitudeSnapshot ?? 0;
        var taken = (int)decimal.Floor(Math.Min(raw, Math.Max(0, cap - used)));
        if (taken > 0)
            await statuses.ApplyAsync(battle.Room, "Character", actor.Id, mirror.BudgetStatusCode, 0, [], "",
                perTickValue: budget.PerTickValue, boundTargetType: "Monster", boundTargetId: battle.Monster.Id,
                magnitudeSnapshot: used + taken);
        return taken;
    }

    private async Task EndReflectionMirrorRoundAsync(Room room, Monster monster, ReflectionMirrorOptions mirror, List<string> logs)
    {
        if (await FindAsync(room, monster) is not { } state) return;
        if (monster.Hp <= 0)
        {
            state.IsActive = false;
            state.RewardStartsAtRound = null;
            await statuses.RemoveAsync(room, "Monster", monster.Id, mirror.MirrorStatusCode);
            await RemoveMirrorAmplificationAsync(room, monster, mirror);
            return;
        }
        if (!state.IsActive) return;
        if (room.RoundNumber < state.ExpiresAfterRound)
        {
            var phaseRound = room.RoundNumber - state.LastActivationRound!.Value + 1;
            if (phaseRound <= mirror.GrowthRounds &&
                await statuses.StacksAsync(room, "Monster", monster.Id, mirror.AmplificationStatusCode) < phaseRound)
            {
                // A persisted, separate counter advances once at each eligible round end. It never repairs the mirror.
                await statuses.ApplyAsync(room, "Monster", monster.Id, mirror.AmplificationStatusCode,
                    state.ExpiresAfterRound - room.RoundNumber, logs, monster.Name,
                    source: new("Monster", monster.Id, "reflection-mirror-amplification"),
                    boundTargetType: "Monster", boundTargetId: monster.Id, counterCount: phaseRound);
            }
            return;
        }
        state.IsActive = false;
        state.ExpiryCount++;
        state.LastExpiryRound = room.RoundNumber;
        await statuses.RemoveAsync(room, "Monster", monster.Id, mirror.MirrorStatusCode);
        await RemoveMirrorAmplificationAsync(room, monster, mirror);
        logs.Add($"{monster.Name} 的琉辉反镜自然结束，恢复常态。");
    }

    private Task RemoveMirrorAmplificationAsync(Room room, Monster monster, ReflectionMirrorOptions mirror) =>
        string.IsNullOrEmpty(mirror.AmplificationStatusCode) ? Task.CompletedTask :
            statuses.RemoveAsync(room, "Monster", monster.Id, mirror.AmplificationStatusCode);
}
