using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed partial class MonsterPhaseService
{
    public EarthArmorOptions? EarthArmorDefinition(Room room, Monster monster) =>
        (runRules?.CombatFor(room) ?? catalog).FindProfile(monster.CombatProfileCode)?.EarthArmor;

    public static long RequiredWindDamage(Monster monster, EarthArmorOptions armor) =>
        (long)decimal.Ceiling(monster.MaxHp * armor.BreakWindDamagePercent / 100m);

    private async Task BeginEarthArmorRoundAsync(Room room, Monster monster, EarthArmorOptions armor, List<string> logs)
    {
        if (monster.Hp <= 0) return;
        // The skill preview and phase preparation share the same persisted encounter clock.
        await EncounterSkillRoundAsync(room, monster);
        var state = (await FindAsync(room, monster))!;
        if (state.LastPreparedRound == room.RoundNumber) return;
        state.LastPreparedRound = room.RoundNumber;
        if (state.RewardStartsAtRound == room.RoundNumber)
        {
            await statuses.ApplyAsync(room, "Monster", monster.Id, armor.RewardStatusCode, armor.RewardRounds - 1,
                logs, monster.Name, source: new("Monster", monster.Id, "earth-armor-break"));
            state.RewardStartsAtRound = null;
        }
        var localRound = room.RoundNumber - state.EncounterStartRound + 1;
        var shouldActivate = armor.TriggerHpPercent is { } hp
            ? state.ActivationCount == 0 && (long)monster.Hp * 100 <= (long)monster.MaxHp * hp
            : localRound >= state.NextActivationRound;
        if (!shouldActivate) return;
        // Keep the cadence after an early break. Old resonance survives until a successful break.
        if (armor.TriggerHpPercent is null)
            while (state.NextActivationRound <= localRound) state.NextActivationRound += armor.CycleRounds;
        state.IsActive = true;
        state.ElementDamage = 0;
        state.ExpiresAfterRound = checked(room.RoundNumber + armor.WindowRounds - 1);
        state.LastActivationRound = room.RoundNumber;
        state.ActivationCount++;
        await statuses.ApplyAsync(room, "Monster", monster.Id, armor.ArmorStatusCode, armor.WindowRounds - 1,
            logs, monster.Name, source: new("Monster", monster.Id, "earth-armor-activation"));
        logs.Add($"{monster.Name} 生成矿脉护甲：{armor.WindowRounds}回合内累计造成 {RequiredWindDamage(monster, armor)} 点风属性直接伤害可破甲。");
    }

    private async Task ObserveEarthArmorDamageAsync(BattleExecutionContext battle, EarthArmorOptions armor,
        ElementType? element, int actualDamage)
    {
        if (element != ElementType.Wind || actualDamage <= 0) return;
        var state = await FindAsync(battle.Room, battle.Monster);
        if (state is null || !state.IsActive || state.ExpiresAfterRound < battle.Room.RoundNumber) return;
        state.ElementDamage += actualDamage;
        if (state.ElementDamage < RequiredWindDamage(battle.Monster, armor)) return;
        state.IsActive = false;
        state.BreakCount++;
        state.LastBreakRound = battle.Room.RoundNumber;
        state.RewardStartsAtRound = checked(battle.Room.RoundNumber + 1);
        await statuses.RemoveAsync(battle.Room, "Monster", battle.Monster.Id, armor.ArmorStatusCode);
        if (!string.IsNullOrEmpty(armor.ResonanceStatusCode))
            await statuses.RemoveAsync(battle.Room, "Monster", battle.Monster.Id, armor.ResonanceStatusCode);
        var cleared = string.IsNullOrEmpty(armor.ResonanceStatusCode) ? "减伤立即解除" : "减伤与地脉共鸣立即清除";
        battle.Logs.Add($"{battle.Monster.Name} 的矿脉护甲被击碎，{cleared}；接下来完整{armor.RewardRounds}回合获得破甲增伤窗口。");
    }

    private async Task EndEarthArmorRoundAsync(Room room, Monster monster, EarthArmorOptions armor, List<string> logs)
    {
        if (await FindAsync(room, monster) is not { IsActive: true } state) return;
        if (monster.Hp <= 0) { state.IsActive = false; return; }
        if (room.RoundNumber < state.ExpiresAfterRound)
        {
            await GrowEarthResonanceAsync(room, monster, armor, state, logs);
            return;
        }
        state.IsActive = false;
        state.ExpiryCount++;
        state.LastExpiryRound = room.RoundNumber;
        await statuses.RemoveAsync(room, "Monster", monster.Id, armor.ArmorStatusCode);
        var aftermath = string.IsNullOrEmpty(armor.ResonanceStatusCode) ? "恢复常态" : "已有地脉共鸣保留，停止增长";
        logs.Add($"{monster.Name} 的矿脉护甲自然消散，{aftermath}。");
    }

    private async Task GrowEarthResonanceAsync(Room room, Monster monster, EarthArmorOptions armor,
        BattleMonsterPhaseState state, List<string> logs)
    {
        if (string.IsNullOrEmpty(armor.ResonanceStatusCode) || state.LastActivationRound is not { } start) return;
        var definition = statuses.CatalogFor(room).Find(armor.ResonanceStatusCode)!;
        // Derive the desired stack count from committed local rounds, so repeated end/reload calls cannot grow twice.
        var desired = Math.Min(definition.MaxStacks, room.RoundNumber - start + 1);
        if (desired <= await statuses.StacksAsync(room, "Monster", monster.Id, armor.ResonanceStatusCode)) return;
        await statuses.SetCounterAsync(room, "Monster", monster.Id, armor.ResonanceStatusCode, desired,
            source: new("Monster", monster.Id, "earth-resonance-growth"), boundTargetType: "Monster", boundTargetId: monster.Id);
        logs.Add($"{monster.Name} 的{definition.Name}积累至{desired}层（攻击+{definition.ValuePerStack * desired:0.##}%），成功破甲可清除。");
    }
}
