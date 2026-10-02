using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed partial class MonsterPhaseService
{
    public FireCoreOptions? Definition(Room room, Monster monster) => Profile(room, monster)?.FireCore;

    private async Task BeginFireCoreRoundAsync(Room room, Monster monster, FireCoreOptions core, List<string> logs)
    {
        if (monster.Hp <= 0) return;
        var state = await EnsureStateAsync(room, monster);
        if (!MonsterPhaseLifecycle.PrepareRound(state, room.RoundNumber)) return;
        if (state.RewardStartsAtRound == room.RoundNumber)
        {
            await statuses.ApplyAsync(room, "Monster", monster.Id, core.RewardStatusCode, core.RewardRounds - 1,
                logs, monster.Name, source: new("Monster", monster.Id, "fire-core-break"));
            state.RewardStartsAtRound = null;
        }
        if (!MonsterPhaseLifecycle.ShouldActivate(core, state, monster, room.RoundNumber)) return;
        MonsterPhaseLifecycle.Activate(core, state, room.RoundNumber);
        await statuses.ApplyAsync(room, "Monster", monster.Id, core.HeatingStatusCode, core.WindowRounds - 1,
            logs, monster.Name, source: new("Monster", monster.Id, "fire-core-activation"));
        logs.Add($"{monster.Name} 开始蓄热：{core.WindowRounds}回合内累计造成 {RequiredDamage(monster, core)} 点水属性直接伤害可破核。");
    }

    public static long RequiredDamage(Monster monster, FireCoreOptions core) =>
        (long)decimal.Ceiling(monster.MaxHp * core.BreakWaterDamagePercent / 100m);

    private async Task ObserveFireCoreDamageAsync(BattleExecutionContext battle, FireCoreOptions core,
        ElementType? element, int actualDamage)
    {
        if (element != ElementType.Water || actualDamage <= 0) return;
        var state = await FindAsync(battle.Room, battle.Monster);
        if (state is null || !state.IsActive || state.ExpiresAfterRound < battle.Room.RoundNumber) return;
        state.ElementDamage += actualDamage;
        if (state.ElementDamage < RequiredDamage(battle.Monster, core)) return;
        MonsterPhaseLifecycle.Complete(state, battle.Room.RoundNumber);
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
            MonsterPhaseLifecycle.ShouldActivate(core, state, monster, room.RoundNumber);
    }

    public async Task RecordLinkedHitAsync(Room room, Monster monster)
    {
        if (await FindAsync(room, monster) is { } state) state.LinkedHitCount++;
    }

    private async Task EndFireCoreRoundAsync(Room room, Monster monster, FireCoreOptions core, List<string> logs)
    {
        if (await FindAsync(room, monster) is not { IsActive: true } state) return;
        if (monster.Hp <= 0) { state.IsActive = false; return; }
        if (room.RoundNumber < state.ExpiresAfterRound) return;
        state.IsActive = false;
        state.ExpiryCount++;
        state.LastExpiryRound = room.RoundNumber;
        await statuses.RemoveAsync(room, "Monster", monster.Id, core.HeatingStatusCode);
        logs.Add($"{monster.Name} 的蓄热结束，恢复常态。");
    }
}
