using System.Text.Json;
using Game.Shared;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Room-local overrides never mutate the tracked base loadout.</summary>
public static class BattleAutoPolicyResolver
{
    public sealed record SkillPolicy(bool AutoUseEnabled, string? AutoConditionOverride, int AutoHpThresholdPercent);
    public sealed class Overrides
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<int, SkillPolicy> Skills { get; set; } = [];
        public Dictionary<int, SkillPolicy> Consumables { get; set; } = [];
        public bool? SoulAutoUseEnabled { get; set; }
        public SkillPolicy? Soul { get; set; }
    }

    private static Overrides Read(RoomSlot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.AutoPolicyOverridesJson)) return new();
        var value = JsonSerializer.Deserialize<Overrides>(slot.AutoPolicyOverridesJson)
            ?? throw new InvalidOperationException("InvalidAutoPolicySnapshot");
        if (value.SchemaVersion != 1 || value.Skills is null || value.Consumables is null)
            throw new InvalidOperationException("UnsupportedAutoPolicySnapshot");
        if (value.Skills.Any(pair => pair.Key is < 1 or > SkillRules.SlotCount || pair.Value is null ||
                pair.Value.AutoHpThresholdPercent is < 1 or > 100 ||
                !SkillAutoRules.IsValidOverride(pair.Value.AutoConditionOverride)))
            throw new InvalidOperationException("InvalidAutoPolicySnapshot");
        if (value.Consumables.Any(pair => pair.Key is < 1 or > ConsumableRules.SlotCount || pair.Value is null ||
                pair.Value.AutoHpThresholdPercent is < 1 or > 100 ||
                !SkillAutoRules.IsValidOverride(pair.Value.AutoConditionOverride)))
            throw new InvalidOperationException("InvalidAutoPolicySnapshot");
        if (value.Soul is { } soul && (soul.AutoHpThresholdPercent is < 1 or > 100 ||
            !SkillAutoRules.IsValidOverride(soul.AutoConditionOverride)))
            throw new InvalidOperationException("InvalidAutoPolicySnapshot");
        return value;
    }

    public static string? ValidationError(RoomSlot slot)
    {
        try { Read(slot); return null; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { return "InvalidBattleAutoPolicySnapshot"; }
    }

    public static CharacterSkillSlot Skill(RoomSlot roomSlot, CharacterSkillSlot source)
    {
        var policy = Read(roomSlot).Skills.GetValueOrDefault(source.SlotIndex);
        return new CharacterSkillSlot
        {
            Id = source.Id, CharacterId = source.CharacterId, SlotIndex = source.SlotIndex,
            SkillCode = source.SkillCode, Version = source.Version,
            AutoUseEnabled = policy?.AutoUseEnabled ?? source.AutoUseEnabled,
            AutoConditionOverride = policy is null ? source.AutoConditionOverride : policy.AutoConditionOverride,
            AutoHpThresholdPercent = policy?.AutoHpThresholdPercent ?? source.AutoHpThresholdPercent
        };
    }

    public static SkillPolicy Soul(RoomSlot slot, CharacterSoulImprint source)
    {
        var value = Read(slot);
        return value.Soul ?? new(value.SoulAutoUseEnabled ?? source.AutoUseEnabled,
            source.AutoConditionOverride, source.AutoHpThresholdPercent);
    }

    public static CharacterConsumableSlot Consumable(RoomSlot roomSlot, CharacterConsumableSlot source)
    {
        var policy = Read(roomSlot).Consumables.GetValueOrDefault(source.SlotIndex);
        return new CharacterConsumableSlot
        {
            Id = source.Id, CharacterId = source.CharacterId, SlotIndex = source.SlotIndex,
            ItemCode = source.ItemCode, Version = source.Version,
            AutoUseEnabled = policy?.AutoUseEnabled ?? source.AutoUseEnabled,
            AutoConditionOverride = policy is null ? source.AutoConditionOverride : policy.AutoConditionOverride,
            AutoHpThresholdPercent = policy?.AutoHpThresholdPercent ?? source.AutoHpThresholdPercent
        };
    }

    public static string WithConsumable(RoomSlot slot, int index, bool enabled, string? condition, int threshold)
    {
        var value = Read(slot);
        value.Consumables[index] = new(enabled, condition, threshold);
        return JsonSerializer.Serialize(value);
    }

    public static bool SoulAuto(RoomSlot slot, bool baseValue)
    {
        var value = Read(slot);
        return value.Soul?.AutoUseEnabled ?? value.SoulAutoUseEnabled ?? baseValue;
    }
    public static string WithSkill(RoomSlot slot, int index, bool enabled, string? condition, int threshold)
    {
        var value = Read(slot);
        value.Skills[index] = new(enabled, condition, threshold);
        return JsonSerializer.Serialize(value);
    }
    public static string WithSoul(RoomSlot slot, bool enabled)
    {
        var value = Read(slot);
        value.SoulAutoUseEnabled = enabled;
        if (value.Soul is { } soul) value.Soul = soul with { AutoUseEnabled = enabled };
        return JsonSerializer.Serialize(value);
    }

    public static string WithSoul(RoomSlot slot, bool enabled, string? condition, int threshold)
    {
        var value = Read(slot);
        value.Soul = new(enabled, condition, threshold);
        value.SoulAutoUseEnabled = enabled;
        return JsonSerializer.Serialize(value);
    }
}
