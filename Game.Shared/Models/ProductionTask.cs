namespace Game.Shared.Models;

public sealed class ProductionTask
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int CharacterId { get; set; }
    public string RecipeCode { get; set; } = string.Empty;
    public string OutputCode { get; set; } = string.Empty;
    public int OutputQuantity { get; set; }
    public int ExtraYieldChancePercent { get; set; }
    public int IngredientSaveChancePercent { get; set; }
    public int SavedIngredientQuantity { get; set; }
    public int ExtraYieldQuantity { get; set; }
    public string IngredientsJson { get; set; } = string.Empty;
    public int CycleSeconds { get; set; }
    public string Status { get; set; } = "Running";
    public DateTime StartedAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public DateTime NextCycleAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int CompletedCycles { get; set; }
    public int TotalQuantity { get; set; }
    public int Version { get; set; }
}
