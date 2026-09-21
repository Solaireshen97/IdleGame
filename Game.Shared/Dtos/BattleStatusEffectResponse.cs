namespace Game.Shared.Dtos;

public sealed class BattleStatusEffectResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsPositive { get; set; }
    public int Stacks { get; set; }
    public int RemainingRounds { get; set; }
}

public sealed class MonsterIntentResponse
{
    public string ActionType { get; set; } = string.Empty;
    public string? SkillCode { get; set; }
    public string ActionName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public int? TargetCharacterId { get; set; }
    public string TargetLabel { get; set; } = string.Empty;
}
