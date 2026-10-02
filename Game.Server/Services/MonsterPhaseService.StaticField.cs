using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed partial class MonsterPhaseService
{
    public StaticFieldOptions? StaticFieldDefinition(Room room, Monster monster) =>
        (runRules?.CombatFor(room) ?? catalog).FindProfile(monster.CombatProfileCode)?.StaticField;

    private async Task BeginStaticFieldRoundAsync(Room room, Monster monster, StaticFieldOptions field, List<string> logs)
    {
        if (monster.Hp <= 0) return;
        await EncounterSkillRoundAsync(room, monster);
        var state = (await FindAsync(room, monster))!;
        if (state.LastPreparedRound == room.RoundNumber) return;
        state.LastPreparedRound = room.RoundNumber;
        if (state.RewardStartsAtRound == room.RoundNumber)
        {
            await statuses.ApplyAsync(room, "Monster", monster.Id, field.RewardStatusCode, field.RewardRounds - 1,
                logs, monster.Name, source: new("Monster", monster.Id, "static-field-break"));
            state.RewardStartsAtRound = null;
        }
        var localRound = room.RoundNumber - state.EncounterStartRound + 1;
        var shouldActivate = field.TriggerHpPercent is { } hp
            ? state.ActivationCount == 0 && (long)monster.Hp * 100 <= (long)monster.MaxHp * hp
            : localRound >= state.NextActivationRound;
        if (!shouldActivate) return;
        // Success, timeout and discharge never move the fixed next activation.
        if (field.TriggerHpPercent is null)
            while (state.NextActivationRound <= localRound) state.NextActivationRound += field.CycleRounds;
        await statuses.RemoveAsync(room, "Monster", monster.Id, field.StaticStatusCode);
        await statuses.RemoveAsync(room, "Monster", monster.Id, field.GrowthUsedStatusCode);
        await RemoveStaticThunderPreviewAsync(room, monster, field);
        state.IsActive = true;
        state.ActivationCount++;
        state.LastActivationRound = room.RoundNumber;
        state.ExpiresAfterRound = checked(room.RoundNumber + field.WindowRounds - 1);
        await statuses.ApplyAsync(room, "Monster", monster.Id, field.StaticStatusCode, field.WindowRounds - 1,
            logs, monster.Name, source: new("Monster", monster.Id, "static-field-activation"),
            boundTargetType: "Monster", boundTargetId: monster.Id, counterCount: field.InitialStacks);
        logs.Add($"{monster.Name} 开启静电领域：初始{field.InitialStacks}层，窗口{field.WindowRounds}回合。每名角色每回合首次有效{WeaponRules.ElementName(field.RemovalElement)}属性直接伤害消除一层，主动清零后获得反攻窗口。");
    }

    private async Task ObserveStaticFieldDamageAsync(BattleExecutionContext battle, StaticFieldOptions field,
        ElementType? element, int damage, int? characterId)
    {
        if (damage <= 0 || element != field.RemovalElement || characterId is not { } id || battle.Monster.Hp <= 0 ||
            battle.Party.All(p => p.Character.Id != id || p.Character.Hp <= 0) ||
            await FindAsync(battle.Room, battle.Monster) is not { IsActive: true } state ||
            state.ExpiresAfterRound < battle.Room.RoundNumber ||
            await statuses.HasAsync(battle.Room, "Character", id, field.HitUsedStatusCode)) return;
        if (await statuses.ConsumeAsync(battle.Room, "Monster", battle.Monster.Id, field.StaticStatusCode, 1) == 0) return;
        await statuses.ApplyAsync(battle.Room, "Character", id, field.HitUsedStatusCode, 0, [], "",
            boundTargetType: "Monster", boundTargetId: battle.Monster.Id);
        state.LinkedHitCount++;
        var remaining = await statuses.StacksAsync(battle.Room, "Monster", battle.Monster.Id, field.StaticStatusCode);
        battle.Logs.Add($"{WeaponRules.ElementName(element!.Value)}属性命中消除一层静电，剩余{remaining}层。");
        if (remaining > 0) return;
        state.IsActive = false;
        state.BreakCount++;
        state.LastBreakRound = battle.Room.RoundNumber;
        state.RewardStartsAtRound = checked(battle.Room.RoundNumber + 1);
        await statuses.RemoveAsync(battle.Room, "Monster", battle.Monster.Id, field.GrowthUsedStatusCode);
        await RemoveStaticThunderPreviewAsync(battle.Room, battle.Monster, field);
        battle.Logs.Add($"{battle.Monster.Name} 的静电被主动清空，领域立即结束；下一完整回合起获得{field.RewardRounds}回合风暴失衡反攻窗口。");
    }

    public async Task<decimal> StaticFieldSkillBonusAsync(Room room, Monster monster, string skillCode)
    {
        if (StaticFieldDefinition(room, monster) is not { } field || field.AmplifiedSkillCode != skillCode ||
            await FindAsync(room, monster) is not { IsActive: true } state || state.ExpiresAfterRound < room.RoundNumber) return 0;
        return field.SkillDamagePercentPerStack * await statuses.StacksAsync(room, "Monster", monster.Id, field.StaticStatusCode);
    }

    // The preview is committed at the preceding round end. Selection does not roll or consume it.
    public async Task<string?> PendingStaticThunderAsync(Room room, Monster monster)
    {
        if (StaticFieldDefinition(room, monster) is not { ThunderAtStacks: > 0 } field || monster.Hp <= 0 ||
            await FindAsync(room, monster) is not { IsActive: true } state || state.ExpiresAfterRound < room.RoundNumber ||
            !await statuses.HasAsync(room, "Monster", monster.Id, field.ThunderPendingStatusCode)) return null;
        return field.ThunderSkillCode;
    }

    public async Task<bool> CanReleaseStaticThunderAsync(Room room, Monster monster)
    {
        if (await PendingStaticThunderAsync(room, monster) is null || StaticFieldDefinition(room, monster) is not { } field) return false;
        return await statuses.StacksAsync(room, "Monster", monster.Id, field.StaticStatusCode) >= field.ThunderAtStacks;
    }

    public async Task FinishStaticThunderAsync(Room room, Monster monster, List<string> logs, bool released)
    {
        if (StaticFieldDefinition(room, monster) is not { ThunderAtStacks: > 0 } field) return;
        await RemoveStaticThunderPreviewAsync(room, monster, field);
        if (!released) { logs.Add($"{monster.Name} 的静电已降到{field.ThunderAtStacks}层以下，雷暴取消，本回合行动结束。"); return; }
        if (await FindAsync(room, monster) is not { IsActive: true } state) return;
        state.IsActive = false;
        await statuses.RemoveAsync(room, "Monster", monster.Id, field.StaticStatusCode);
        await statuses.RemoveAsync(room, "Monster", monster.Id, field.GrowthUsedStatusCode);
        logs.Add($"{monster.Name} 的雷暴释放完毕，剩余静电清空、领域结束；未获得主动清零奖励。");
    }

    private Task RemoveStaticThunderPreviewAsync(Room room, Monster monster, StaticFieldOptions field) =>
        string.IsNullOrEmpty(field.ThunderPendingStatusCode) ? Task.CompletedTask :
            statuses.RemoveAsync(room, "Monster", monster.Id, field.ThunderPendingStatusCode);

    private async Task EndStaticFieldRoundAsync(Room room, Monster monster, StaticFieldOptions field, List<string> logs)
    {
        if (await FindAsync(room, monster) is not { IsActive: true } state) return;
        if (monster.Hp <= 0 || room.RoundNumber >= state.ExpiresAfterRound)
        {
            state.IsActive = false;
            if (monster.Hp > 0)
            {
                state.ExpiryCount++;
                state.LastExpiryRound = room.RoundNumber;
                logs.Add($"{monster.Name} 的静电领域自然结束，剩余静电消散。");
            }
            await statuses.RemoveAsync(room, "Monster", monster.Id, field.StaticStatusCode);
            await statuses.RemoveAsync(room, "Monster", monster.Id, field.GrowthUsedStatusCode);
            await RemoveStaticThunderPreviewAsync(room, monster, field);
            return;
        }
        var phaseRound = room.RoundNumber - state.LastActivationRound!.Value + 1;
        if (phaseRound <= field.GrowthRounds &&
            await statuses.StacksAsync(room, "Monster", monster.Id, field.GrowthUsedStatusCode) < phaseRound)
        {
            // Persist growth progress separately so a repeated settlement/reload cannot add stacks twice.
            await statuses.SetCounterAsync(room, "Monster", monster.Id, field.GrowthUsedStatusCode, phaseRound,
                boundTargetType: "Monster", boundTargetId: monster.Id);
            var stacks = await statuses.IncreaseStacksAsync(room, "Monster", monster.Id, field.StaticStatusCode, field.GrowthStacksPerRound);
            logs.Add($"{monster.Name} 的静电积累至{stacks}层；前{field.GrowthRounds}个机制回合之后停止增长。");
        }
        if (field.ThunderAtStacks > 0 &&
            await statuses.StacksAsync(room, "Monster", monster.Id, field.StaticStatusCode) >= field.ThunderAtStacks &&
            !await statuses.HasAsync(room, "Monster", monster.Id, field.ThunderPendingStatusCode))
        {
            await statuses.ApplyAsync(room, "Monster", monster.Id, field.ThunderPendingStatusCode, 1, logs, monster.Name,
                source: new("Monster", monster.Id, "static-field-thunder-preview"), boundTargetType: "Monster", boundTargetId: monster.Id);
            logs.Add($"{monster.Name} 的静电达到{field.ThunderAtStacks}层，下一回合预告雷暴；出手前降层可取消。");
        }
    }
}
