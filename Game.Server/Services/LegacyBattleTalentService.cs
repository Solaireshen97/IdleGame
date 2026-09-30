using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Preserves stored historical nodes; the current profession system does not sell or award them.</summary>
public sealed class LegacyBattleTalentService(GameDbContext db, SkillCatalog skills, BattleStatusService statuses)
{
    public async Task<Dictionary<int, Dictionary<string, int>>> RanksAsync(IReadOnlyCollection<int> ids) =>
        (await db.CharacterSkillTalents.Where(node => ids.Contains(node.CharacterId)).ToListAsync())
        .GroupBy(node => node.CharacterId).ToDictionary(group => group.Key,
            group => group.ToDictionary(node => node.NodeCode, node => node.PointsSpent, StringComparer.OrdinalIgnoreCase));

    private decimal Power(string code) => code switch
    {
        "talent-intercept-echo" => skills.LegacyTalentValue("sword-disruption") ?? 50,
        "talent-guard-echo" => skills.LegacyTalentValue("sword-counteroffense") ?? 75,
        _ => 25
    };

    public Task<bool> GrantEchoAsync(Room room, int characterId, string code, int rounds, string sourceSkill) =>
        statuses.ApplyAsync(room, "Character", characterId, code, rounds, [], string.Empty,
            source: new("Character", characterId, sourceSkill), magnitudeSnapshot: Power(code), counterCount: 1);

    public async Task<decimal> ConsumeEchoAsync(Room room, int characterId)
    {
        var total = 0m;
        foreach (var state in await statuses.MechanicStatesAsync(room, "Character", characterId, BattleStatusMechanic.NormalAttackEcho))
        {
            if (state.EffectCode != "talent-guard-echo" && state.AppliedRound >= room.RoundNumber) continue;
            using var action = statuses.Events.ActionScope(new BattleStatusSource("Character", characterId, "normal-attack"), "消费追击", BattleActionKind.Mechanic);
            total += state.MagnitudeSnapshot ?? Power(state.EffectCode);
            await statuses.ConsumeAsync(room, "Character", characterId, state.EffectCode);
        }
        return total;
    }

    public async Task VictoryAsync(BattleExecutionContext battle, IReadOnlyCollection<int> characterIds)
    {
        var room = battle.Room;
        var eligible = await db.CharacterSkillTalents.Where(node => characterIds.Contains(node.CharacterId) &&
            node.NodeCode == "sword-pursuit" && node.PointsSpent > 0).Select(node => node.CharacterId).ToListAsync();
        if (eligible.Count == 0) return;
        var cooldowns = await db.BattleSkillCooldowns.Where(item => item.RoomId == room.Id && eligible.Contains(item.CharacterId)).ToListAsync();
        var affected = cooldowns.Where(item => skills.FindDefinition(item.SkillCode)?.Effects.Any(effect => effect.Kind == BattleEffectKind.Damage) == true).ToList();
        var changed = new HashSet<int>();
        foreach (var cooldown in affected)
        {
            var ready = Math.Max(room.RoundNumber + 1, cooldown.ReadyAtRound - 1);
            if (ready < cooldown.ReadyAtRound) changed.Add(cooldown.CharacterId);
            cooldown.ReadyAtRound = ready;
        }
        foreach (var actor in battle.Characters.Where(actor => eligible.Contains(actor.Id) && actor.Hp > 0))
        {
            using var action = statuses.Events.ActionScope(actor, "sword-pursuit", "乘胜追击", BattleActionKind.Mechanic);
            var before = actor.Hp;
            var calculated = (int)decimal.Floor(actor.MaxHp * .08m);
            var recovered = BattleDamageService.RestoreHp(actor, calculated);
            statuses.Events.Hp(room, BattleEventKind.Heal, actor, actor, calculated, recovered, before);
            if (changed.Contains(actor.Id)) statuses.Events.Utility(room, BattleEventKind.Cooldown, actor, 1, "冷却缩短 1 回合");
            battle.Logs.Add(recovered > 0
                ? $"{actor.Name} 的乘胜追击恢复了 {recovered} 点生命，并使伤害技能冷却缩短 1 回合。"
                : $"{actor.Name} 的乘胜追击使伤害技能冷却缩短 1 回合。");
        }
    }
}
