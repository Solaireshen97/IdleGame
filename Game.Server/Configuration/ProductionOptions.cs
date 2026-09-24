namespace Game.Server.Configuration;

public sealed class ProductionOptions
{
    public const string SectionName = "Production";
    public List<ProductionRecipeOptions> Recipes { get; set; } = [];
}

public sealed class ProductionRecipeOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OutputCode { get; set; } = string.Empty;
    public int OutputQuantity { get; set; } = 1;
    public int CycleSeconds { get; set; } = 10;
    public int MinimumCharacterLevel { get; set; } = 1;
    public int MinimumAlchemyLevel { get; set; } = 1;
    public string UnlockKind { get; set; } = string.Empty;
    public string UnlockTargetCode { get; set; } = string.Empty;
    public List<string> AlternativeUnlockTargetCodes { get; set; } = [];
    public int RequiredCount { get; set; } = 1;
    public List<ProductionIngredientOptions> Ingredients { get; set; } = [];
}

public sealed class ProductionIngredientOptions
{
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; }
}
