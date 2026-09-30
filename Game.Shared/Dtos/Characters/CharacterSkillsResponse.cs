namespace Game.Shared.Dtos.Characters;

public class CharacterSkillsResponse
{
    public int CharacterId { get; set; }
    public string ProfessionCode { get; set; } = string.Empty;
    public string ProfessionName { get; set; } = string.Empty;
    public int Level { get; set; }
    public List<LearnedSkillResponse> LearnedSkills { get; set; } = [];
    public List<SharedSkillResponse> SharedSkills { get; set; } = [];
    public List<EquippedSkillResponse> Slots { get; set; } = [];
}

public class LearnedSkillResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty;
    public int Power { get; set; }
    public int CooldownRounds { get; set; }
    public int InitialCooldownRounds { get; set; }
    public int Level { get; set; }
    public int UnlockLevel { get; set; }
    public int Level2UnlockLevel { get; set; }
    public int Level3UnlockLevel { get; set; }
    public bool IsShared { get; set; }
    public string SourceProfessionCode { get; set; } = string.Empty;
    public string AutoCondition { get; set; } = "Always";
    public List<SkillEffectResponse> Effects { get; set; } = [];
}

public class SharedSkillResponse : LearnedSkillResponse
{
    public bool CanEquip { get; set; }
}

public class SkillEffectResponse
{
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Power { get; set; }
    public decimal AttackPowerPercent { get; set; }
    public decimal HealMaxHpPercent { get; set; }
    public string? StatusCode { get; set; }
    public int DurationRounds { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public string? StatusDescription { get; set; }
    public bool? StatusIsPositive { get; set; }
}

public class EquippedSkillResponse
{
    public int SlotIndex { get; set; }
    public string? SkillCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public string AutoCondition { get; set; } = "Always";
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; }
}

public class SetSkillSlotRequest
{
    public string? SkillCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; } = SkillRules.DefaultAutoHpThresholdPercent;
}

public class SetSkillAutoRequest
{
    public bool AutoUseEnabled { get; set; }
    public string? AutoConditionOverride { get; set; }
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
    public string? GrantedSkillName { get; set; }
}

public class PromoteCharacterRequest
{
    public string ProfessionCode { get; set; } = string.Empty;
}
