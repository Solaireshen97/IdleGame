using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class RoomSlotResponse
{
    public int SlotIndex { get; set; }
    public int? CharacterId { get; set; }
    public int? PendingConsumableSlotIndex { get; set; }
    public int PendingSkillSlotMask { get; set; }
    public bool IsSoulImprintQueued { get; set; }
    public RoomSoulImprintResponse? SoulImprint { get; set; }
    public List<RoomSkillSlotResponse> Skills { get; set; } = [];
    public List<RoomConsumableSlotResponse> Consumables { get; set; } = [];
    public RoomOperationPotionResponse? OperationPotion { get; set; }
    public string? CharacterName { get; set; }
    public ElementType? CharacterElement { get; set; }
    public int OutgoingElementModifierPercent { get; set; }
    public int IncomingElementModifierPercent { get; set; }
    public string? ProfessionName { get; set; }
    public int? CharacterHp { get; set; }
    public int? CharacterMaxHp { get; set; }
    public int? CharacterLevel { get; set; }
    public int? CharacterExperience { get; set; }
    public int? ExperienceToNextLevel { get; set; }
    public int? TalentPoints { get; set; }
    public bool IsOccupied { get; set; }
    public bool IsMainControl { get; set; }
    public bool IsCurrentUserCharacter { get; set; }
    public bool IsAlive { get; set; }
    public bool IsConfirmed { get; set; }
    public string? PlayerName { get; set; }
    public bool IsAutoEnabled { get; set; }
    public bool IsTemporaryAuto { get; set; }
    public bool IsOffline { get; set; }
    public bool IsOfflineAuto { get; set; }
    public bool IsAutoUnlockedForCurrentUser { get; set; }
    public bool CanConfigureAuto { get; set; }
    public List<BattleStatusEffectResponse> StatusEffects { get; set; } = [];
}

public sealed class RoomSoulImprintResponse
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ElementType Element { get; set; }
    public SoulImprintEffectType EffectType { get; set; }
    public int PowerPercent { get; set; }
    public int SecondaryPowerPercent { get; set; }
    public int DurationRounds { get; set; }
    public int InitialCooldownRounds { get; set; }
    public int CooldownRounds { get; set; }
    public int CooldownRoundsRemaining { get; set; }
    public bool AutoUseEnabled { get; set; }
}

public class RoomSkillSlotResponse
{
    public int SlotIndex { get; set; }
    public string? SkillCode { get; set; }
    public string? SkillName { get; set; }
    public string? Description { get; set; }
    public string? EffectType { get; set; }
    public int Power { get; set; }
    public string AutoCondition { get; set; } = "Always";
    public List<Game.Shared.Dtos.Characters.SkillEffectResponse> Effects { get; set; } = [];
    public int CooldownRoundsRemaining { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; }
}

public class RoomConsumableSlotResponse
{
    public int SlotIndex { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? Kind { get; set; }
    public string? Description { get; set; }
    public int HealAmount { get; set; }
    public int Quantity { get; set; }
    public int CooldownRoundsRemaining { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; }
}

public sealed class RoomOperationPotionResponse
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? Description { get; set; }
    public int Quantity { get; set; }
    public int AttackPercent { get; set; }
    public bool HasAttempted { get; set; }
    public bool IsActive { get; set; }
}
