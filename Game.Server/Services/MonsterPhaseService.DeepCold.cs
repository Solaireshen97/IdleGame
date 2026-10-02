using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed partial class MonsterPhaseService
{
    public DeepColdOptions? DeepColdDefinition(Room room, Monster monster) =>
        (runRules?.CombatFor(room) ?? catalog).FindProfile(monster.CombatProfileCode)?.DeepCold;

    private string DeepColdStatusName(Room room, string statusCode) =>
        (runRules?.CombatFor(room) ?? catalog).Statuses.Find(statusCode)!.Name;

    private async Task BeginDeepColdRoundAsync(Room room, Monster monster, DeepColdOptions cold,
        List<string> logs, IReadOnlyList<BattleParticipant>? participants)
    {
        if (monster.Hp <= 0) return;
        await EncounterSkillRoundAsync(room, monster);
        var state = (await FindAsync(room, monster))!;
        if (state.LastPreparedRound == room.RoundNumber) return;
        state.LastPreparedRound = room.RoundNumber;
        var characters = participants is not null
            ? participants.Select(p => p.Character).Where(c => c.Hp > 0).ToList()
            : await (from slot in db.RoomSlots join character in db.Characters on slot.CharacterId equals character.Id
                where slot.RoomId == room.Id && character.Hp > 0 orderby slot.SlotIndex select character).ToListAsync();
        foreach (var character in characters)
        {
            if (cold.FreezeAtStacks > 0 && await statuses.HasAsync(room, "Character", character.Id, cold.FreezePendingStatusCode))
            {
                await statuses.RemoveAsync(room, "Character", character.Id, cold.FreezePendingStatusCode);
                if (state.IsActive && state.ExpiresAfterRound >= room.RoundNumber &&
                    await statuses.StacksAsync(room, "Character", character.Id, cold.ColdStatusCode) >= cold.FreezeAtStacks)
                    await statuses.ApplyAsync(room, "Character", character.Id, cold.FreezeStatusCode, 0,
                        logs, character.Name, source: new("Monster", monster.Id, "deep-cold-freeze"),
                        boundTargetType: "Monster", boundTargetId: monster.Id);
            }
            var pending = (await statuses.GetActiveAsync(room, "Character", [character.Id]))
                .FirstOrDefault(s => s.EffectCode == cold.PendingStatusCode && s.BoundTargetId == monster.Id &&
                    s.AppliedRound < room.RoundNumber);
            if (pending is null) continue;
            await statuses.RemoveAsync(room, "Character", character.Id, cold.PendingStatusCode);
            await statuses.ApplyAsync(room, "Character", character.Id, cold.WarmStatusCode, cold.RewardRounds - 1,
                logs, character.Name, source: new("Monster", monster.Id, "deep-cold-reward"),
                boundTargetType: "Monster", boundTargetId: monster.Id);
        }
        var localRound = room.RoundNumber - state.EncounterStartRound + 1;
        var shouldActivate = cold.TriggerHpPercent is { } hp
            ? state.ActivationCount == 0 && (long)monster.Hp * 100 <= (long)monster.MaxHp * hp
            : !state.IsActive && localRound >= state.NextActivationRound;
        if (characters.Count == 0 || !shouldActivate) return;
        // Clearing early preserves the original cadence. Personal success/freeze limits restart each cycle.
        if (cold.TriggerHpPercent is null)
        {
            while (state.NextActivationRound <= localRound) state.NextActivationRound += cold.CycleRounds;
            await statuses.RemoveCodesAsync(room, "Character", characters.Select(c => c.Id).ToArray(),
                new[] { cold.ColdStatusCode, cold.ClearedStatusCode, cold.PendingStatusCode, cold.MeltUsedStatusCode,
                    cold.FreezeStatusCode, cold.FreezePendingStatusCode, cold.FreezeUsedStatusCode }
                    .Where(code => code.Length > 0).ToArray());
        }
        state.IsActive = true;
        state.ActivationCount++;
        state.LastActivationRound = room.RoundNumber;
        state.ExpiresAfterRound = checked(room.RoundNumber + cold.WindowRounds - 1);
        await statuses.ApplyAsync(room, "Monster", monster.Id, cold.FieldStatusCode, cold.WindowRounds - 1,
            logs, monster.Name, source: new("Monster", monster.Id, "deep-cold-activation"));
        foreach (var character in characters)
        {
            await statuses.RemoveCodesAsync(room, "Character", [character.Id], cold.BasicColdStatusCodes);
            await statuses.ApplyAsync(room, "Character", character.Id, cold.ColdStatusCode, cold.WindowRounds - 1,
                logs, character.Name, source: new("Monster", monster.Id, "deep-cold-activation"),
                boundTargetType: "Monster", boundTargetId: monster.Id, counterCount: cold.InitialStacks);
        }
        logs.Add($"{monster.Name} 释放{DeepColdStatusName(room, cold.FieldStatusCode)}：全队获得{cold.InitialStacks}层{DeepColdStatusName(room, cold.ColdStatusCode)}，{cold.WindowRounds}回合内主动清除可获得{DeepColdStatusName(room, cold.WarmStatusCode)}。每名角色每回合首次有效{WeaponRules.ElementName(cold.RemovalElement)}属性直接伤害为全队破除一层冰层束缚，净化清除单人的全部层数。");
    }

    private async Task<BattleMonsterPhaseState?> ActiveDeepColdAsync(Room room, Monster monster) =>
        monster.Hp > 0 && await FindAsync(room, monster) is { IsActive: true } state &&
        state.ExpiresAfterRound >= room.RoundNumber ? state : null;

    private async Task MeltDeepColdAsync(BattleExecutionContext battle, int sourceCharacterId, ElementType damageElement)
    {
        if (DeepColdDefinition(battle.Room, battle.Monster) is not { } cold || cold.RemovalElement != damageElement ||
            battle.Party.All(p => p.Character.Id != sourceCharacterId || p.Character.Hp <= 0) ||
            await ActiveDeepColdAsync(battle.Room, battle.Monster) is not { } state ||
            await statuses.HasAsync(battle.Room, "Character", sourceCharacterId, cold.MeltUsedStatusCode)) return;
        await statuses.ApplyAsync(battle.Room, "Character", sourceCharacterId, cold.MeltUsedStatusCode, 0, [], "",
            boundTargetType: "Monster", boundTargetId: battle.Monster.Id);
        var melted = 0;
        foreach (var participant in battle.Party.Where(p => p.Character.Hp > 0).OrderBy(p => p.Slot.SlotIndex))
        {
            var id = participant.Character.Id;
            if (await statuses.ConsumeAsync(battle.Room, "Character", id, cold.ColdStatusCode, 1) == 0) continue;
            melted++;
            var stacks = await statuses.StacksAsync(battle.Room, "Character", id, cold.ColdStatusCode);
            if (cold.FreezeAtStacks > 0 && stacks < cold.FreezeAtStacks)
                await ClearColdFreezeAsync(battle.Room, id, cold);
            if (stacks == 0)
                await GrantWarmAsync(battle, participant.Character, cold, state);
        }
        if (melted > 0)
        {
            state.LinkedHitCount++;
            battle.Logs.Add($"{WeaponRules.ElementName(cold.RemovalElement)}属性攻击击碎冰层，为{melted}名队友各消除一层{DeepColdStatusName(battle.Room, cold.ColdStatusCode)}。");
        }
    }

    public async Task ObserveDeepColdCleanseAsync(BattleExecutionContext battle, int characterId, string statusCode)
    {
        if (DeepColdDefinition(battle.Room, battle.Monster) is not { } cold || statusCode != cold.ColdStatusCode ||
            await ActiveDeepColdAsync(battle.Room, battle.Monster) is not { } state) return;
        var character = battle.Party.FirstOrDefault(p => p.Character.Id == characterId)?.Character;
        if (character is { Hp: > 0 }) await GrantWarmAsync(battle, character, cold, state);
    }

    private async Task GrantWarmAsync(BattleExecutionContext battle, Character character, DeepColdOptions cold,
        BattleMonsterPhaseState state)
    {
        if (await statuses.HasAsync(battle.Room, "Character", character.Id, cold.ClearedStatusCode)) return;
        await ClearColdFreezeAsync(battle.Room, character.Id, cold);
        await statuses.ApplyAsync(battle.Room, "Character", character.Id, cold.ClearedStatusCode, 0, [], "",
            boundTargetType: "Monster", boundTargetId: battle.Monster.Id);
        await statuses.ApplyAsync(battle.Room, "Character", character.Id, cold.PendingStatusCode, 1, [], "",
            source: new("Monster", battle.Monster.Id, "deep-cold-success"),
            boundTargetType: "Monster", boundTargetId: battle.Monster.Id);
        state.BreakCount++;
        state.LastBreakRound = battle.Room.RoundNumber;
        battle.Logs.Add($"{character.Name} 主动解除了{DeepColdStatusName(battle.Room, cold.ColdStatusCode)}，下一完整回合起获得{cold.RewardRounds}回合{DeepColdStatusName(battle.Room, cold.WarmStatusCode)}增伤，并免疫基础寒冷；本次阶段不会再次受到冰层束缚。");
    }

    public async Task<bool> SuppressBasicColdAsync(Room room, Monster monster, int characterId, string statusCode)
    {
        if (DeepColdDefinition(room, monster) is not { } cold || !cold.BasicColdStatusCodes.Contains(statusCode)) return false;
        return await ActiveDeepColdAsync(room, monster) is not null ||
            await statuses.HasAsync(room, "Character", characterId, cold.WarmStatusCode);
    }

    private async Task EndDeepColdRoundAsync(Room room, Monster monster, DeepColdOptions cold, List<string> logs)
    {
        if (await FindAsync(room, monster) is not { IsActive: true } state) return;
        if (monster.Hp > 0 && room.RoundNumber < state.ExpiresAfterRound)
        {
            await GrowDeepColdAsync(room, monster, cold, logs);
            return;
        }
        state.IsActive = false;
        state.ExpiryCount++;
        state.LastExpiryRound = room.RoundNumber;
        var characterIds = (await db.RoomSlots.Where(s => s.RoomId == room.Id && s.CharacterId.HasValue)
            .Select(s => s.CharacterId!.Value).ToListAsync());
        await statuses.RemoveCodesAsync(room, "Character", characterIds,
            new[] { cold.ColdStatusCode, cold.FreezeStatusCode, cold.FreezePendingStatusCode }.Where(c => c.Length > 0).ToArray());
        await statuses.RemoveAsync(room, "Monster", monster.Id, cold.FieldStatusCode);
        if (monster.Hp > 0) logs.Add($"{monster.Name} 的{DeepColdStatusName(room, cold.FieldStatusCode)}阶段结束；剩余{DeepColdStatusName(room, cold.ColdStatusCode)}自然消散，已获得的{DeepColdStatusName(room, cold.WarmStatusCode)}继续生效。");
    }

    private Task ClearColdFreezeAsync(Room room, int characterId, DeepColdOptions cold) => cold.FreezeAtStacks == 0
        ? Task.CompletedTask
        : statuses.RemoveCodesAsync(room, "Character", [characterId], [cold.FreezeStatusCode, cold.FreezePendingStatusCode]);

    private async Task GrowDeepColdAsync(Room room, Monster monster, DeepColdOptions cold, List<string> logs)
    {
        if (cold.GrowthStacksPerRound == 0 || await statuses.HasAsync(room, "Monster", monster.Id, cold.GrowthUsedStatusCode)) return;
        await statuses.ApplyAsync(room, "Monster", monster.Id, cold.GrowthUsedStatusCode, 0, [], "",
            boundTargetType: "Monster", boundTargetId: monster.Id);
        var characters = await (from slot in db.RoomSlots join character in db.Characters on slot.CharacterId equals character.Id
            where slot.RoomId == room.Id && character.Hp > 0 orderby slot.SlotIndex select character).ToListAsync();
        foreach (var character in characters)
        {
            var stacks = await statuses.IncreaseStacksAsync(room, "Character", character.Id, cold.ColdStatusCode, cold.GrowthStacksPerRound);
            if (stacks == 0) continue;
            logs.Add($"{character.Name} 的{DeepColdStatusName(room, cold.ColdStatusCode)}在回合结束时积累至{stacks}层。");
            if (stacks < cold.FreezeAtStacks || await statuses.HasAsync(room, "Character", character.Id, cold.FreezeUsedStatusCode)) continue;
            await statuses.ApplyAsync(room, "Character", character.Id, cold.FreezeUsedStatusCode, 0, [], "",
                boundTargetType: "Monster", boundTargetId: monster.Id);
            await statuses.ApplyAsync(room, "Character", character.Id, cold.FreezePendingStatusCode, 1, [], "",
                boundTargetType: "Monster", boundTargetId: monster.Id);
            logs.Add($"{character.Name} 的{DeepColdStatusName(room, cold.ColdStatusCode)}达到{DeepColdStatusName(room, cold.FreezeStatusCode)}阈值，下一回合冰封一次；破冰使层数降至{cold.FreezeAtStacks}层以下或净化可解除。");
        }
    }
}
