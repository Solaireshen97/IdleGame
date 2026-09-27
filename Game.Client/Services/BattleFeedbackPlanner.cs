using System.Text.RegularExpressions;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;

namespace Game.Client.Services;

// A presentation adapter for the existing server battle log. Unknown messages never
// invent an attacker or a critical hit; authoritative HP is restored after playback.
public static partial class BattleFeedbackPlanner
{
    public static bool SameEncounter(RoomDetailResponse a, RoomDetailResponse b) =>
        a.RoomId == b.RoomId && a.RunSequence == b.RunSequence &&
        a.CurrentWaveNumber == b.CurrentWaveNumber && a.CurrentEnemyNumber == b.CurrentEnemyNumber &&
        a.MonsterName == b.MonsterName;

    public static BattleFeedbackPlan? Create(RoomDetailResponse? before, RoomDetailResponse after)
    {
        if (before is null || before.ClosedAtUtc.HasValue || before.RoomId != after.RoomId ||
            before.RunSequence != after.RunSequence || before.MonsterHp <= 0 ||
            after.RoundNumber != before.RoundNumber + 1) return null;

        var lastId = before.BattleLogs.Select(log => log.Id).DefaultIfEmpty().Max();
        var newLogs = after.BattleLogs.Where(log => log.Id > lastId).OrderBy(log => log.Id).ToList();
        var sameEnemy = SameEncounter(before, after);
        // The server switches MonsterId in the killing round itself, before its
        // transition countdown. Replay that round against the OLD enemy snapshot.
        var recordedDefeat = newLogs.Any(log => log.Text == $"{before.MonsterName} 已被击败。");
        if (!sameEnemy && !recordedDefeat) return null;
        var defeated = recordedDefeat || sameEnemy && after.MonsterHp <= 0;
        if (after.ClosedAtUtc.HasValue && !defeated) return null;
        var finalEnemyHp = defeated ? 0 : after.MonsterHp;
        var events = new List<BattleFeedbackEvent>();
        BattleFeedbackEvent? previous = null;
        foreach (var log in newLogs)
        {
            var current = Parse(log.Text, before, after);
            if (current is { IsFollowUp: true })
                current = current with { IsFollowUp = previous is { Kind: "damage", Label: "普通攻击" or "二连击" } &&
                    previous.Source == current.Source && previous.Target == current.Target };
            if (current is not null) events.Add(current);
            previous = current;
        }
        if (events.Count == 0 && before.MonsterHp > finalEnemyHp)
            events.Add(new("", "enemy", before.MonsterHp - finalEnemyHp, "damage", "pulse", "neutral", "回合伤害", false));
        if (events.Count == 0) return null;

        var vitals = after.Slots.Where(slot => slot.IsOccupied).ToDictionary(
            slot => slot.SlotIndex.ToString(), slot => new BattleFeedbackVitals(slot.CharacterHp ?? 0, slot.CharacterMaxHp ?? 1));
        vitals["enemy"] = new(finalEnemyHp, before.MonsterMaxHp);
        return new(after.RoundNumber, events, vitals, defeated,
            sameEnemy && after.RoomStatus == RoomStatus.BattleOver && defeated);
    }

    public static bool CanFinishPlayback(RoomDetailResponse scene, RoomDetailResponse latest, BattleFeedbackPlan plan) =>
        scene.RoomId == latest.RoomId && scene.RunSequence == latest.RunSequence && latest.RoundNumber == plan.Round &&
        (plan.Defeated || !latest.ClosedAtUtc.HasValue && SameEncounter(scene, latest));

    private static BattleFeedbackEvent? Parse(string text, RoomDetailResponse room, RoomDetailResponse latest)
    {
        var slots = room.Slots.Where(slot => slot.IsOccupied).ToList();
        var actor = slots.FirstOrDefault(slot => text.StartsWith(Prefix(slot), StringComparison.Ordinal));
        var action = actor is null ? text : text[Prefix(actor).Length..];
        var damage = DamageAmount().Match(action);
        var heal = HealAmount().Match(action);
        var source = actor?.SlotIndex.ToString() ?? "";
        var tone = actor?.CharacterElement?.ToString().ToLowerInvariant() ?? "neutral";
        var style = actor?.ProfessionName switch
        {
            "法师" or "元素使" or "奥术师" => "magic",
            "祭司" or "牧师" or "审判官" => "holy",
            "猎人" or "神射手" or "兽王" => "arrow",
            "盗贼" or "刺客" or "诡术师" => "dagger",
            _ => "slash"
        };
        var label = ActionLabel(action);

        var gained = GainedStatus().Match(actor is null && text.StartsWith(room.MonsterName + " ", StringComparison.Ordinal)
            ? text[(room.MonsterName.Length + 1)..] : action);
        if (gained.Success && (actor is not null || text.StartsWith(room.MonsterName + " ", StringComparison.Ordinal)))
        {
            var target = actor is null ? "enemy" : source;
            var name = gained.Groups[1].Value;
            var effects = actor is null ? latest.MonsterEffects.Concat(room.MonsterEffects) :
                (latest.Slots.FirstOrDefault(slot => slot.CharacterId == actor.CharacterId)?.StatusEffects ?? [])
                    .Concat(actor.StatusEffects);
            var status = effects.FirstOrDefault(effect => effect.Name == name);
            var kind = status is null ? "status" : status.IsPositive ? "buff" : "debuff";
            return new("", target, 0, kind, "pulse", kind == "buff" ? "light" : kind == "debuff" ? "dark" : "neutral", name, false);
        }
        if (action.Contains("驱散了 " + room.MonsterName + " 的 ", StringComparison.Ordinal))
            return new(source, "enemy", 0, "dispel", "pulse", "light", "强化驱散", false);
        if (action.Contains("移除了 ", StringComparison.Ordinal))
        {
            var recipient = slots.FirstOrDefault(slot => text.Contains($"移除了 {Prefix(slot).TrimEnd()} 的 ", StringComparison.Ordinal)) ?? actor;
            if (recipient is not null)
                return new(source, recipient.SlotIndex.ToString(), 0, "cleanse", "pulse", "heal", "净化", false);
        }
        var cooldown = ReducedCooldown().Match(action);
        if (actor is not null && cooldown.Success)
            return new(source, source, 0, "cooldown", "pulse", "light", $"冷却缩短 {cooldown.Groups[1].Value} 回合", false);
        if (actor is not null && action.StartsWith("使用 ", StringComparison.Ordinal))
        {
            var boosts = DamageBoost().Matches(action);
            if (boosts.Count > 0)
                return new(source, source, 0, "buff", "pulse", "light", string.Join(" · ", boosts.Select(match => match.Value)), false);
        }

        if (heal.Success && int.TryParse(heal.Groups[1].Value, out var healing))
        {
            var recipient = slots.FirstOrDefault(slot => action.Contains($"为 {Prefix(slot)}", StringComparison.Ordinal)) ?? actor;
            if (recipient is null || healing <= 0) return null;
            return new(source, recipient.SlotIndex.ToString(), healing, "heal", "pulse", "heal", "生命恢复", false);
        }

        if (damage.Success && int.TryParse(damage.Groups[1].Value, out var amount) && amount > 0)
        {
            if (actor is not null && action.StartsWith("受到 ", StringComparison.Ordinal))
                return new("", source, amount, "damage", "pulse", "hostile", "持续伤害", false);

            if (actor is null)
            {
                var target = slots.FirstOrDefault(slot => text.Contains($"攻击 {Prefix(slot).TrimEnd()}，", StringComparison.Ordinal));
                if (target is not null && text.StartsWith(room.MonsterName + " ", StringComparison.Ordinal))
                    return new("enemy", target.SlotIndex.ToString(), amount, "damage", "slash",
                        room.MonsterElement.ToString().ToLowerInvariant(), "敌方反击", false, target.IncomingElementModifierPercent);
                // Environmental damage and soul echoes have no character prefix.
                if (!text.Contains(room.MonsterName, StringComparison.Ordinal)) return null;
                return new("", "enemy", amount, "damage", "pulse", "neutral", "追加伤害", false);
            }
            var modifier = actor.OutgoingElementModifierPercent;
            if (action.StartsWith("释放魂印", StringComparison.Ordinal))
            {
                style = "magic";
                // Soul imprints use their own element, including other players'
                // imprints whose private loadout is absent from the room response.
                var element = SoulElement().Match(action).Groups[1].Value switch
                {
                    "火" => ElementType.Fire, "水" => ElementType.Water, "土" => ElementType.Earth,
                    "风" => ElementType.Wind, "光" => ElementType.Light, "暗" => ElementType.Dark,
                    _ => (ElementType?)null
                };
                tone = element?.ToString().ToLowerInvariant() ?? "neutral";
                modifier = ElementMatchupRules.PlayerAttackPercent(element, room.MonsterElement);
            }
            // Fixed poison damage and the talent counter do not use the elemental
            // multiplier. Normal attack echoes inherit their originating hit.
            if (action.Contains("无视防御伤害", StringComparison.Ordinal) || action.StartsWith("招架后反击", StringComparison.Ordinal))
            { tone = "neutral"; modifier = 0; }
            var normalEcho = action.StartsWith($"对 {room.MonsterName} 造成 ", StringComparison.Ordinal) &&
                action.EndsWith("点普攻追击伤害。", StringComparison.Ordinal);
            return new(source, "enemy", amount, "damage", style, tone, normalEcho ? "普攻追击" : label,
                action.EndsWith("（暴击）。", StringComparison.Ordinal), modifier, normalEcho);
        }

        if (action.Contains($"打断了 {room.MonsterName} 的行动", StringComparison.Ordinal))
            return new(source, "enemy", 0, "interrupt", "pulse", "light", "打断", false);
        if (actor is not null && action.Contains("，守护 ", StringComparison.Ordinal))
        {
            var target = slots.FirstOrDefault(slot => action.Contains($"守护 {Prefix(slot).TrimEnd()}", StringComparison.Ordinal));
            if (target is not null) return new(source, target.SlotIndex.ToString(), 0, "guard", "pulse", "water", "守护", false);
        }
        return null;
    }

    private static string Prefix(RoomSlotResponse slot) => $"{slot.SlotIndex}号位 {slot.CharacterName} ";

    private static string ActionLabel(string action)
    {
        if (action.StartsWith("普通攻击", StringComparison.Ordinal)) return "普通攻击";
        if (action.StartsWith("二连击", StringComparison.Ordinal)) return "二连击";
        if (action.StartsWith("招架后反击", StringComparison.Ordinal)) return "招架反击";
        var skill = SkillName().Match(action);
        if (skill.Success) return skill.Groups[1].Value;
        var soul = SoulName().Match(action);
        return soul.Success ? soul.Groups[1].Value : "追击";
    }

    [GeneratedRegex(@"(?:造成(?:的)?|追加) (\d+) 点[^，。]*伤害")]
    private static partial Regex DamageAmount();
    [GeneratedRegex(@"恢复 (\d+) 点生命值")]
    private static partial Regex HealAmount();
    [GeneratedRegex(@"^(?:使用|触发) (.+?) 攻击 ")]
    private static partial Regex SkillName();
    [GeneratedRegex("^释放魂印「(.+?)」")]
    private static partial Regex SoulName();
    [GeneratedRegex("点([火水土风光暗])属性伤害")]
    private static partial Regex SoulElement();
    [GeneratedRegex(@"^获得 (.+?)，持续 \d+ 回合")]
    private static partial Regex GainedStatus();
    [GeneratedRegex(@"冷却缩短 (\d+) 回合")]
    private static partial Regex ReducedCooldown();
    [GeneratedRegex(@"(?:普通攻击伤害|最终伤害|攻击) \+\d+(?:\.\d+)?%")]
    private static partial Regex DamageBoost();
}

public sealed record BattleFeedbackEvent(string Source, string Target, int Amount, string Kind,
    string Style, string Tone, string Label, bool Critical, int ElementModifierPercent = 0, bool IsFollowUp = false);
public sealed record BattleFeedbackVitals(int Hp, int MaxHp);
public sealed record BattleFeedbackPlan(int Round, List<BattleFeedbackEvent> Events,
    Dictionary<string, BattleFeedbackVitals> FinalVitals, bool Defeated, bool Victory)
{
    public long TotalDamage => Events.Where(e => e.Target == "enemy" && e.Kind == "damage").Sum(e => (long)e.Amount);
    public int HitCount => Events.Count(e => e.Target == "enemy" && e.Kind == "damage");
    public int CriticalCount => Events.Count(e => e.Critical);
}
