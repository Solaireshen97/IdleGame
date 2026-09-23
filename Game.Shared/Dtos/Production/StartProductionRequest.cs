namespace Game.Shared.Dtos.Production;

public sealed class StartProductionRequest
{
    public int CharacterId { get; set; }
    public string RecipeCode { get; set; } = string.Empty;
}
