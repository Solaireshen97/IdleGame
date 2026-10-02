using Game.Shared.Enums;
using Game.Shared.Dtos.Characters;

namespace Game.Shared.Dtos.Formations;

public sealed class CombatLoadoutDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfessionCode { get; set; } = "swordsman";
    public List<FormationWeaponChoice> Weapons { get; set; } = [];
    public List<FormationSkillChoice> Skills { get; set; } = [];
    public List<FormationConsumableChoice> Consumables { get; set; } = [];
    public int? SoulImprintId { get; set; }
    public bool SoulAutoUseEnabled { get; set; }
    public string? SoulAutoConditionOverride { get; set; }
    public int SoulAutoHpThresholdPercent { get; set; } = SkillRules.DefaultAutoHpThresholdPercent;
}
public sealed class FormationWeaponChoice { public int SlotIndex { get; set; } public int? WeaponId { get; set; } }
public sealed class FormationSkillChoice
{
    public int SlotIndex { get; set; }
    public string? SkillCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; } = 70;
}
public sealed class FormationConsumableChoice
{
    public int SlotIndex { get; set; }
    public string? ItemCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; } = ConsumableRules.DefaultAutoHpThresholdPercent;
}
public sealed class LoadoutSelection
{
    public string Mode { get; set; } = "Current";
    public int? FormationId { get; set; }
    public int? ExpectedVersion { get; set; }
    public bool RememberForEncounter { get; set; } = true;
}
public sealed class FormationIssue
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "Error";
    public string Section { get; set; } = "";
    public int? SlotIndex { get; set; }
    public string Message { get; set; } = "";
}
public sealed class FormationPreviewResponse
{
    public bool CanDeploy { get; set; }
    public string ProfessionName { get; set; } = "";
    public int Level { get; set; }
    public int Attack { get; set; }
    public int MaxHp { get; set; }
    public ElementType? MainElement { get; set; }
    public FormationMainWeaponResponse? MainWeapon { get; set; }
    public int WeaponAttack { get; set; }
    public int WeaponMaxHp { get; set; }
    public decimal WeaponAttackBonusPercent { get; set; }
    public decimal WeaponHealthBonusPercent { get; set; }
    public decimal WeaponCriticalChancePercent { get; set; }
    public List<ActiveWeaponSkillResponse> WeaponSkills { get; set; } = [];
    public List<WeaponEffectResponse> WeaponEffects { get; set; } = [];
    public List<FormationIssue> Issues { get; set; } = [];
    public List<LearnedSkillResponse> AvailableSkills { get; set; } = [];
    public List<FormationSkillLibraryEntry> SkillLibrary { get; set; } = [];
}
public sealed class FormationSkillLibraryEntry : LearnedSkillResponse
{
    public bool IsUnlocked { get; set; }
    public bool CanEquip { get; set; }
    public string SourceProfessionName { get; set; } = "";
    public int SourceProfessionLevel { get; set; }
    public int RequiredProfessionLevel { get; set; }
    public int RequiredCurrentProfessionLevel { get; set; }
    public List<LearnedSkillResponse> LevelPreviews { get; set; } = [];
}
public sealed class FormationMainWeaponResponse
{
    public int WeaponId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public ElementType Element { get; set; }
    public int QualityRank { get; set; }
}
public sealed class FormationResponse
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string Name { get; set; } = "";
    public ElementType GroupElement { get; set; }
    public int Position { get; set; }
    public int Version { get; set; }
    public CombatLoadoutDefinition Loadout { get; set; } = new();
    public FormationPreviewResponse Preview { get; set; } = new();
}
public sealed class FormationOverviewResponse
{
    public int CharacterId { get; set; }
    public int CharacterVersion { get; set; }
    public bool CanApply { get; set; }
    public int PositionsPerElement { get; set; } = 6;
    public int? DefaultFormationId { get; set; }
    public int? AppliedFormationId { get; set; }
    public int? AppliedFormationVersion { get; set; }
    public bool CurrentIsModified { get; set; }
    public CombatLoadoutDefinition CurrentLoadout { get; set; } = new();
    public List<FormationResponse> Formations { get; set; } = [];
}
public class SaveFormationRequest
{
    public string Name { get; set; } = "";
    public ElementType GroupElement { get; set; }
    public int Position { get; set; } = 1;
    public CombatLoadoutDefinition Loadout { get; set; } = new();
    public int? ExpectedVersion { get; set; }
}
public sealed class CreateFormationRequest : SaveFormationRequest { public bool FromCurrent { get; set; } }
public sealed class CopyFormationRequest
{
    public int ExpectedVersion { get; set; }
    public string Name { get; set; } = "";
    public ElementType GroupElement { get; set; }
    public int Position { get; set; } = 1;
}
public sealed class FormationVersionRequest { public int ExpectedVersion { get; set; } }
public sealed class SetDefaultFormationRequest { public int? FormationId { get; set; } public int? ExpectedVersion { get; set; } }
public sealed class ApplyFormationRequest
{
    public int ExpectedVersion { get; set; }
    public int ExpectedCharacterVersion { get; set; }
    public string RequestId { get; set; } = "";
}
public sealed class FormationRecommendationResponse
{
    public LoadoutSelection Selection { get; set; } = new();
    public string Source { get; set; } = "Current";
    public List<FormationIssue> Issues { get; set; } = [];
}
