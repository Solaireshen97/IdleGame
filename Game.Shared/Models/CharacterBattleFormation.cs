using Game.Shared.Enums;

namespace Game.Shared.Models;

public sealed class CharacterBattleFormation
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public ElementType GroupElement { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public string ProfessionCode { get; set; } = "swordsman";
    public int? SoulImprintId { get; set; }
    public bool SoulAutoUseEnabled { get; set; }
    public string? SoulAutoConditionOverride { get; set; }
    public int SoulAutoHpThresholdPercent { get; set; } = SkillRules.DefaultAutoHpThresholdPercent;
    public int Version { get; set; } = 1;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<FormationWeaponSlot> Weapons { get; set; } = [];
    public List<FormationSkillSlot> Skills { get; set; } = [];
    public List<FormationConsumableSlot> Consumables { get; set; } = [];
}
public sealed class FormationWeaponSlot { public int FormationId { get; set; } public int SlotIndex { get; set; } public int? WeaponId { get; set; } }
public sealed class FormationSkillSlot
{
    public int FormationId { get; set; }
    public int SlotIndex { get; set; }
    public string? SkillCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; } = 70;
}
public sealed class FormationConsumableSlot
{
    public int FormationId { get; set; }
    public int SlotIndex { get; set; }
    public string? ItemCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; } = 50;
}
public sealed class CharacterFormationState
{
    public int CharacterId { get; set; }
    public int? DefaultFormationId { get; set; }
    public int? AppliedFormationId { get; set; }
    public int? AppliedFormationVersion { get; set; }
    public string? AppliedChoiceHash { get; set; }
    public int Version { get; set; }
}
public sealed class CharacterBattleFormationPreference
{
    public int CharacterId { get; set; }
    public string DungeonCode { get; set; } = "";
    public int DepthLevel { get; set; } = 1;
    public int FormationId { get; set; }
    public int Version { get; set; }
    public DateTime LastUsedAtUtc { get; set; }
}
public sealed class BattleAdmissionReceipt
{
    public int UserId { get; set; }
    public string RequestId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int? RoomId { get; set; }
    public int CharacterId { get; set; }
    public string? ResultJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

