namespace Game.Shared.Dtos.Production;

public sealed class ProductionOverviewResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public int AlchemyLevel { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public List<ProductionRecipeResponse> Recipes { get; set; } = [];
    public ProductionTaskResponse? ActiveTask { get; set; }
    public List<ProductionTaskResponse> RecentTasks { get; set; } = [];
}

public sealed class ProductionRecipeResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OutputCode { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public int OutputQuantity { get; set; }
    public int CharacterQuantity { get; set; }
    public int WarehouseQuantity { get; set; }
    public int CycleSeconds { get; set; }
    public int MinimumCharacterLevel { get; set; }
    public int MinimumAlchemyLevel { get; set; }
    public string UnlockDescription { get; set; } = string.Empty;
    public int UnlockProgress { get; set; }
    public int UnlockRequired { get; set; }
    public bool IsUnlocked { get; set; }
    public List<ProductionIngredientResponse> Ingredients { get; set; } = [];
}

public sealed class ProductionIngredientResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int CharacterQuantity { get; set; }
    public int WarehouseQuantity { get; set; }
}

public sealed class ProductionTaskResponse
{
    public int Id { get; set; }
    public string RecipeCode { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int CycleSeconds { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public DateTime NextCycleAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int CompletedCycles { get; set; }
    public int TotalQuantity { get; set; }
}
