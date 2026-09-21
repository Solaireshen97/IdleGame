using Game.Shared.Enums;

namespace Game.Shared.Dtos.Characters;

public class CharacterSkillsResponse
{
    public int CharacterId { get; set; }
    public string ProfessionCode { get; set; } = string.Empty;
    public string ProfessionName { get; set; } = string.Empty;
    public int TalentPoints { get; set; }
    public List<LearnedSkillResponse> LearnedSkills { get; set; } = [];
    public List<EquippedSkillResponse> Slots { get; set; } = [];
    public List<SkillTalentNodeResponse> TalentNodes { get; set; } = [];
}

public class SkillTalentNodeResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SkillCode { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public string SkillDescription { get; set; } = string.Empty;
    public int Cost { get; set; }
    public int Tier { get; set; }
    public int Column { get; set; }
    public TalentType RequiredTalentType { get; set; }
    public int RequiredTalentRank { get; set; }
    public List<string> Prerequisites { get; set; } = [];
    public bool IsUnlocked { get; set; }
    public bool ArePrerequisitesMet { get; set; }
    public bool CanUnlock { get; set; }
}

public class LearnedSkillResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty;
    public int Power { get; set; }
    public int CooldownRounds { get; set; }
    public string AutoCondition { get; set; } = "Always";
    public List<SkillEffectResponse> Effects { get; set; } = [];
}

public class SkillEffectResponse
{
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Power { get; set; }
    public string? StatusCode { get; set; }
    public int DurationRounds { get; set; }
}

public class EquippedSkillResponse
{
    public int SlotIndex { get; set; }
    public string? SkillCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; }
}

public class SetSkillSlotRequest
{
    public string? SkillCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; } = SkillRules.DefaultAutoHpThresholdPercent;
}

public class SwapSkillSlotsRequest
{
    public int FromSlotIndex { get; set; }
    public int ToSlotIndex { get; set; }
}

public class ProfessionResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
