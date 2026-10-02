using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed partial class MonsterPhaseService
{
    public PlaguePoisonOptions? PlaguePoisonDefinition(Room room, Monster monster) =>
        Profile(room, monster)?.PlaguePoison;

    public static long RequiredLightDamage(Monster monster, PlaguePoisonOptions poison) =>
        (long)decimal.Ceiling(monster.MaxHp * poison.BreakLightDamagePercent / 100m);

    private async Task<IReadOnlyList<BattleParticipant>> PlaguePartyAsync(Room room,
        IReadOnlyList<BattleParticipant>? participants) => participants ??
        await BattlePartyReader.ReadAsync(db, room.Id);

    private async Task<int?> PlagueTargetAsync(Room room, Monster monster, PlaguePoisonOptions poison) =>
        (await statuses.GetActiveAsync(room, "Monster", [monster.Id]))
            .FirstOrDefault(s => s.EffectCode == poison.TargetStatusCode && s.BoundTargetType == "Character")?.BoundTargetId;

    public async Task RefreshPlagueHealthAsync(Room room, Monster monster, IReadOnlyList<BattleParticipant> party)
    {
        await PrepareDefinitionsAsync(room);
        if (PlaguePoisonDefinition(room, monster) is not { ErosionStartStacks: > 0 } poison) return;
        var effects = room.Status == RoomStatus.BattleOver || room.ClosedAtUtc.HasValue || monster.Hp <= 0 ? [] :
            await statuses.GetActiveAsync(room, "Character", party.Select(p => p.Character.Id).ToArray());
        foreach (var participant in party)
        {
            var erosion = effects.FirstOrDefault(e => e.TargetId == participant.Character.Id &&
                e.EffectCode == poison.ErosionStatusCode && e.BoundTargetId == monster.Id);
            participant.Character.BattleMaxHpLimit = erosion?.MagnitudeSnapshot is { } original
                ? Math.Max(1, (int)decimal.Floor(original * (1m - poison.ErosionPercentPerStack * erosion.Stacks / 100m))) : null;
        }
    }

    private async Task ClearPlagueErosionAsync(Room room, PlaguePoisonOptions poison, BattleParticipant target)
    {
        if (poison.ErosionStartStacks == 0) return;
        target.Character.BattleMaxHpLimit = null;
        await statuses.RemoveAsync(room, "Character", target.Character.Id, poison.ErosionStatusCode);
    }

    private async Task RemoveBossBasicPoisonAsync(Room room, Monster monster, PlaguePoisonOptions poison, int characterId)
    {
        var basic = (await statuses.GetActiveAsync(room, "Character", [characterId]))
            .Where(s => poison.BasicPoisonStatusCodes.Contains(s.EffectCode) &&
                s.SourceActorType == "Monster" && s.SourceActorId == monster.Id).ToList();
        foreach (var effect in basic)
            await statuses.RemoveAsync(room, "Character", characterId, effect.EffectCode);
    }

    private async Task BeginPlagueRoundAsync(Room room, Monster monster, PlaguePoisonOptions poison,
        List<string> logs, IReadOnlyList<BattleParticipant>? participants)
    {
        var state = await EnsureStateAsync(room, monster);
        var party = await PlaguePartyAsync(room, participants);
        await RefreshPlagueHealthAsync(room, monster, party);
        if (monster.Hp <= 0) { await ClearPlagueAsync(room, monster, poison, state, party, removeReward: true); return; }
        if (!MonsterPhaseLifecycle.PrepareRound(state, room.RoundNumber)) return;
        if (state.RewardStartsAtRound is { } reward && reward <= room.RoundNumber)
        {
            var targetId = await PlagueTargetAsync(room, monster, poison);
            var target = party.FirstOrDefault(p => p.Character.Id == targetId);
            if (reward == room.RoundNumber && target is { Character.Hp: > 0 })
            {
                await RemoveBossBasicPoisonAsync(room, monster, poison, target.Character.Id);
                await statuses.ApplyAsync(room, "Character", target.Character.Id, poison.RewardStatusCode, poison.RewardRounds - 1,
                    logs, target.Character.Name, source: new("Monster", monster.Id, "plague-poison-reward"),
                    boundTargetType: "Monster", boundTargetId: monster.Id);
            }
            state.RewardStartsAtRound = null;
        }
        if (state.IsActive || !MonsterPhaseLifecycle.ShouldActivate(poison, state, monster, room.RoundNumber)) return;
        var front = party.Where(p => p.Character.Hp > 0).OrderBy(p => p.Slot.SlotIndex).FirstOrDefault();
        if (front is null) return;
        if (poison.TriggerHpPercent is null)
        {
            // Fixed cadence survives breaks/deaths; clear prior counters, bindings and rewards before the next cycle.
            await ClearPlagueAsync(room, monster, poison, state, party, removeReward: true);
        }
        MonsterPhaseLifecycle.Activate(poison, state, room.RoundNumber);
        var modifier = await statuses.ModifierAsync(room, "Monster", monster.Id, "AttackPercent");
        var unit = Math.Max(1, (int)Math.Min(int.MaxValue, decimal.Floor(monster.Attack *
            Math.Max(0, 1m + modifier / 100m) * poison.AttackPercentPerStack / 100m)));
        await statuses.ApplyAsync(room, "Monster", monster.Id, poison.TargetStatusCode, 0, [], "",
            source: new("Monster", monster.Id, "plague-poison-activation"),
            boundTargetType: "Character", boundTargetId: front.Character.Id,
            magnitudeSnapshot: poison.ErosionStartStacks > 0 ? TalentRules.EffectiveMaxHpWithoutBattleLimit(front.Character) : null);
        await RemoveBossBasicPoisonAsync(room, monster, poison, front.Character.Id);
        await statuses.ApplyAsync(room, "Character", front.Character.Id, poison.PoisonStatusCode, poison.WindowRounds - 1,
            logs, front.Character.Name, perTickValue: unit, source: new("Monster", monster.Id, "plague-poison-tick"),
            boundTargetType: "Monster", boundTargetId: monster.Id, counterCount: 1);
        logs.Add($"{monster.Name} 向 {front.Character.Name} 播下疫巢剧毒：从1层开始，每回合末毒伤后增加一层；{poison.WindowRounds}个完整回合内净化或累计 {RequiredLightDamage(monster, poison)} 点光属性直接伤害可解毒。");
        if (poison.LethalStacks > 0)
            logs.Add($"疫巢剧毒达到{poison.ErosionStartStacks}层开始侵蚀生命上限，达到{poison.LethalStacks}层时目标死亡；最后一个处理回合仍可净化或光伤解毒。");
    }

    private async Task ObservePlagueDamageAsync(BattleExecutionContext battle, PlaguePoisonOptions poison,
        ElementType? element, int damage)
    {
        if (element != ElementType.Light || damage <= 0 || battle.Monster.Hp <= 0 ||
            await FindAsync(battle.Room, battle.Monster) is not { IsActive: true } state ||
            state.ExpiresAfterRound < battle.Room.RoundNumber) return;
        var targetId = await PlagueTargetAsync(battle.Room, battle.Monster, poison);
        var target = battle.Party.FirstOrDefault(p => p.Character.Id == targetId);
        if (target is not { Character.Hp: > 0 }) return;
        state.ElementDamage += damage;
        if (state.ElementDamage >= RequiredLightDamage(battle.Monster, poison))
            await BreakPlagueAsync(battle, poison, state, target.Character.Id);
    }

    public async Task ObservePlagueCleanseAsync(BattleExecutionContext battle, int characterId, string code)
    {
        if (PlaguePoisonDefinition(battle.Room, battle.Monster) is not { } poison || code != poison.PoisonStatusCode ||
            battle.Monster.Hp <= 0 || await FindAsync(battle.Room, battle.Monster) is not { IsActive: true } state ||
            state.ExpiresAfterRound < battle.Room.RoundNumber ||
            await PlagueTargetAsync(battle.Room, battle.Monster, poison) != characterId ||
            battle.Party.All(p => p.Character.Id != characterId || p.Character.Hp <= 0) ||
            await statuses.HasAsync(battle.Room, "Character", characterId, poison.PoisonStatusCode)) return;
        await BreakPlagueAsync(battle, poison, state, characterId);
    }

    private async Task BreakPlagueAsync(BattleExecutionContext battle, PlaguePoisonOptions poison,
        BattleMonsterPhaseState state, int targetId)
    {
        MonsterPhaseLifecycle.Complete(state, battle.Room.RoundNumber);
        await statuses.RemoveAsync(battle.Room, "Character", targetId, poison.PoisonStatusCode);
        await ClearPlagueErosionAsync(battle.Room, poison, battle.Party.Single(p => p.Character.Id == targetId));
        await statuses.RemoveAsync(battle.Room, "Monster", battle.Monster.Id, poison.TickUsedStatusCode);
        battle.Logs.Add($"疫巢剧毒已被主动解除；目标从下一完整回合起获得{poison.RewardRounds}回合净蚀庇护，直接减伤并免疫该首领基础腐毒。");
    }

    public async Task<bool> SuppressBasicPoisonAsync(Room room, Monster monster, int characterId, string code)
    {
        if (PlaguePoisonDefinition(room, monster) is not { } poison || !poison.BasicPoisonStatusCodes.Contains(code) || monster.Hp <= 0) return false;
        return await statuses.HasAsync(room, "Character", characterId, poison.RewardStatusCode) ||
            await FindAsync(room, monster) is { IsActive: true } state && state.ExpiresAfterRound >= room.RoundNumber &&
            await PlagueTargetAsync(room, monster, poison) == characterId;
    }

    private async Task EndPlagueRoundAsync(Room room, Monster monster, PlaguePoisonOptions poison, List<string> logs,
        IReadOnlyList<BattleParticipant>? participants, IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses)
    {
        if (await FindAsync(room, monster) is not { } state) return;
        var party = await PlaguePartyAsync(room, participants);
        var targetId = await PlagueTargetAsync(room, monster, poison);
        var target = party.FirstOrDefault(p => p.Character.Id == targetId);
        if (monster.Hp <= 0 || target is not { Character.Hp: > 0 })
        {
            await ClearPlagueAsync(room, monster, poison, state, party, removeReward: true);
            return;
        }
        if (!state.IsActive || state.LastActivationRound is not { } start) return;
        var phaseRound = room.RoundNumber - start + 1;
        if (await statuses.StacksAsync(room, "Monster", monster.Id, poison.TickUsedStatusCode) >= phaseRound) return;
        var dot = (await statuses.GetActiveAsync(room, "Character", [target.Character.Id]))
            .FirstOrDefault(s => s.EffectCode == poison.PoisonStatusCode && s.BoundTargetId == monster.Id);
        if (dot is null) { await ClearPlagueAsync(room, monster, poison, state, party, removeReward: false); return; }
        await statuses.SetCounterAsync(room, "Monster", monster.Id, poison.TickUsedStatusCode, phaseRound,
            boundTargetType: "Monster", boundTargetId: monster.Id);
        var taken = operationBonuses?.GetValueOrDefault(target.Character.Id).DamageTakenPercent ?? 0;
        var damage = Math.Max(1, (int)Math.Min(int.MaxValue, decimal.Floor((dot.PerTickValue ?? 1) *
            (decimal)dot.Stacks * Math.Max(0, 1m + taken / 100m))));
        var before = target.Character.Hp;
        target.Character.Hp = Math.Max(0, before - damage);
        using (statuses.Events.ActionScope(new BattleStatusSource("Monster", monster.Id, "plague-poison-tick"),
            "疫巢剧毒", BattleActionKind.Periodic))
            statuses.Events.Hp(room, BattleEventKind.Damage, null, BattleActor.ForCharacter(target), damage, before - target.Character.Hp, before);
        logs.Add($"{target.Character.Name} 受到{dot.Stacks}层疫巢剧毒，造成{damage}点伤害。");
        if (target.Character.Hp <= 0) { await ClearPlagueAsync(room, monster, poison, state, party, removeReward: true); return; }
        if (room.RoundNumber >= state.ExpiresAfterRound)
        {
            if (poison.LethalStacks > 0)
            {
                await statuses.IncreaseStacksAsync(room, "Character", target.Character.Id, poison.PoisonStatusCode, 1);
                var remaining = target.Character.Hp;
                target.Character.Hp = 0;
                using (statuses.Events.ActionScope(new BattleStatusSource("Monster", monster.Id, "plague-poison-lethal"),
                    "腐巢死亡倒计时", BattleActionKind.Mechanic))
                    statuses.Events.Hp(room, BattleEventKind.Damage, null, BattleActor.ForCharacter(target), remaining, remaining, remaining);
                state.LinkedHitCount++;
                await ClearPlagueAsync(room, monster, poison, state, party, removeReward: true);
                logs.Add($"{target.Character.Name} 的疫巢剧毒达到{poison.LethalStacks}层，腐巢死亡倒计时结束，目标死亡。");
                return;
            }
            state.ExpiryCount++;
            state.LastExpiryRound = room.RoundNumber;
            await ClearPlagueAsync(room, monster, poison, state, party, removeReward: false);
            logs.Add("疫巢剧毒自然消散，本次不发放净蚀庇护。");
            return;
        }
        // Keep the visible lifetime and original expiry; a zero-duration counter write would label it CurrentRound.
        await statuses.ApplyAsync(room, "Character", target.Character.Id, poison.PoisonStatusCode,
            state.ExpiresAfterRound - room.RoundNumber, [], "", perTickValue: dot.PerTickValue,
            source: new("Monster", monster.Id, "plague-poison-tick"), boundTargetType: "Monster", boundTargetId: monster.Id,
            counterCount: phaseRound + 1);
        if (poison.ErosionStartStacks > 0 && phaseRound + 1 >= poison.ErosionStartStacks)
        {
            var anchor = (await statuses.GetActiveAsync(room, "Monster", [monster.Id]))
                .Single(s => s.EffectCode == poison.TargetStatusCode);
            var erosionStacks = phaseRound + 2 - poison.ErosionStartStacks;
            await statuses.ApplyAsync(room, "Character", target.Character.Id, poison.ErosionStatusCode, 0, logs,
                target.Character.Name, source: new("Monster", monster.Id, "plague-poison-erosion"),
                boundTargetType: "Monster", boundTargetId: monster.Id, counterCount: erosionStacks,
                magnitudeSnapshot: anchor.MagnitudeSnapshot);
            await RefreshPlagueHealthAsync(room, monster, party);
            target.Character.Hp = Math.Min(target.Character.Hp, TalentRules.EffectiveMaxHp(target.Character));
            logs.Add($"{target.Character.Name} 的腐巢侵蚀使生命上限降低{erosionStacks * poison.ErosionPercentPerStack:0.##}%，主动解毒可恢复上限。");
        }
        logs.Add($"{target.Character.Name} 的疫巢剧毒积累至{phaseRound + 1}层。");
    }

    private async Task ClearPlagueAsync(Room room, Monster monster, PlaguePoisonOptions poison,
        BattleMonsterPhaseState state, IReadOnlyList<BattleParticipant> party, bool removeReward)
    {
        state.IsActive = false;
        state.RewardStartsAtRound = null;
        foreach (var participant in party)
        {
            await statuses.RemoveAsync(room, "Character", participant.Character.Id, poison.PoisonStatusCode);
            await ClearPlagueErosionAsync(room, poison, participant);
            if (removeReward) await statuses.RemoveAsync(room, "Character", participant.Character.Id, poison.RewardStatusCode);
        }
        await statuses.RemoveAsync(room, "Monster", monster.Id, poison.TargetStatusCode);
        await statuses.RemoveAsync(room, "Monster", monster.Id, poison.TickUsedStatusCode);
    }
}
