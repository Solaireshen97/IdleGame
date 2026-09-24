using Game.Shared.Enums;

namespace Game.Shared.Dtos.Characters;

public sealed class CharacterSoulImprintsResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public List<CharacterSoulImprintResponse> SoulImprints { get; set; } = [];
    public List<WeaponFragmentResponse> Fragments { get; set; } = [];
}

public sealed class CharacterSoulImprintResponse
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Tier { get; set; }
    public ElementType Element { get; set; }
    public SoulImprintEffectType EffectType { get; set; }
    public int PowerPercent { get; set; }
    public int SecondaryPowerPercent { get; set; }
    public int DurationRounds { get; set; }
    public int InitialCooldownRounds { get; set; }
    public int CooldownRounds { get; set; }
    public int DismantleFragments { get; set; }
    public bool IsEquipped { get; set; }
    public bool AutoUseEnabled { get; set; }
    public bool IsLocked { get; set; }
}

public sealed class SetSoulImprintRequest
{
    public int? SoulImprintId { get; set; }
}

public sealed class SetSoulImprintLockRequest
{
    public bool IsLocked { get; set; }
}

public sealed class SetSoulImprintAutoRequest
{
    public bool AutoUseEnabled { get; set; }
}

public sealed class SoulImprintBatchRequest
{
    public List<int> SoulImprintIds { get; set; } = [];
}
