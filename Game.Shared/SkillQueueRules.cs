using System.Text.Json;
using Game.Shared.Models;

namespace Game.Shared;

public static class SkillQueueRules
{
    public static int? TargetCharacterId(RoomSlot slot, int skillSlotIndex) =>
        (slot.PendingSkillSlotMask & SkillRules.SlotMask(skillSlotIndex)) != 0 &&
        ReadTargets(slot).TryGetValue(skillSlotIndex, out var targetId) ? targetId : null;

    public static void Queue(RoomSlot slot, int skillSlotIndex, int? targetCharacterId)
    {
        var targets = ReadTargets(slot);
        slot.PendingSkillSlotMask |= SkillRules.SlotMask(skillSlotIndex);
        if (targetCharacterId.HasValue) targets[skillSlotIndex] = targetCharacterId.Value;
        else targets.Remove(skillSlotIndex);
        SaveTargets(slot, targets);
    }

    public static void Clear(RoomSlot slot, int mask = int.MaxValue)
    {
        slot.PendingSkillSlotMask &= ~mask;
        if (slot.PendingSkillSlotMask == 0) slot.PendingSkillTargetsJson = null;
        else SaveTargets(slot, ReadTargets(slot));
    }

    private static Dictionary<int, int> ReadTargets(RoomSlot slot) =>
        string.IsNullOrEmpty(slot.PendingSkillTargetsJson) ? [] :
            JsonSerializer.Deserialize<Dictionary<int, int>>(slot.PendingSkillTargetsJson) ?? [];

    private static void SaveTargets(RoomSlot slot, Dictionary<int, int> targets)
    {
        var active = targets.Where(entry => (slot.PendingSkillSlotMask & SkillRules.SlotMask(entry.Key)) != 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        slot.PendingSkillTargetsJson = active.Count == 0 ? null : JsonSerializer.Serialize(active);
    }
}
