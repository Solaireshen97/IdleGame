namespace Game.Shared.Dtos.Production;

public sealed class StartProductionRequest
{
    public int CharacterId { get; set; }
    public int? TargetCycles { get; set; }
    public string? RequestId { get; set; }
    public string RecipeCode { get; set; } = string.Empty;
}
