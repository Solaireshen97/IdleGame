using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ProductionCatalog
{
    private readonly Dictionary<string, ProductionRecipeOptions> _recipes = new(StringComparer.OrdinalIgnoreCase);

    public ProductionCatalog(IOptions<ProductionOptions> options, WorldCatalog world,
        MaterialCatalog materials, ConsumableCatalog consumables)
    {
        foreach (var recipe in options.Value.Recipes)
        {
            var source = world.Dungeons.FirstOrDefault(dungeon => dungeon.Code == recipe.UnlockTargetCode && dungeon.IsVisible);
            var alternatives = recipe.AlternativeUnlockTargetCodes;
            var validAlternatives = alternatives.Distinct(StringComparer.OrdinalIgnoreCase).Count() == alternatives.Count &&
                alternatives.All(code => code != recipe.UnlockTargetCode && world.Dungeons.Any(dungeon =>
                    dungeon.Code == code && dungeon.IsVisible &&
                    (recipe.UnlockKind != "DungeonClear" || dungeon.DungeonKind == "Dungeon")));
            if (string.IsNullOrWhiteSpace(recipe.Code) || string.IsNullOrWhiteSpace(recipe.Name) ||
                !_recipes.TryAdd(recipe.Code, recipe) ||
                consumables.FindItem(recipe.OutputCode) is null ||
                recipe.OutputQuantity is < 1 or > 1000 || recipe.CycleSeconds is < 1 or > 3600 ||
                recipe.MinimumCharacterLevel < 1 || recipe.MinimumAlchemyLevel < 1 ||
                recipe.RequiredCount < 1 || source is null || !validAlternatives ||
                recipe.UnlockKind is not ("MonsterKill" or "DungeonClear") ||
                recipe.UnlockKind == "DungeonClear" && source.DungeonKind != "Dungeon" ||
                recipe.Ingredients.Count == 0 ||
                recipe.Ingredients.Select(item => item.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipe.Ingredients.Count ||
                recipe.Ingredients.Any(item => item.Quantity is < 1 or > 1000 ||
                    materials.FindItem(item.Code) is null))
                throw new InvalidOperationException($"Invalid production recipe: {recipe.Code}");
        }
    }

    public IReadOnlyCollection<ProductionRecipeOptions> Recipes => _recipes.Values;
    public ProductionRecipeOptions? FindRecipe(string? code) =>
        code is not null && _recipes.TryGetValue(code, out var recipe) ? recipe : null;
}
