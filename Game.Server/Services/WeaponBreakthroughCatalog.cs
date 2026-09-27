using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class WeaponBreakthroughCatalog
{
    private readonly List<WeaponBreakthroughRecipeOptions> _recipes;

    public WeaponBreakthroughCatalog(IOptions<WeaponBreakthroughOptions> options)
    {
        _recipes = options.Value.Recipes.OrderBy(recipe => recipe.Tier).ToList();
        var tiers = new HashSet<int>();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in _recipes)
        {
            if (recipe.Tier <= 0 || !tiers.Add(recipe.Tier) || recipe.FragmentsPerStone <= 0 ||
                recipe.MinimumWeaponItemLevel <= 0 || recipe.MaximumWeaponItemLevel < recipe.MinimumWeaponItemLevel ||
                string.IsNullOrWhiteSpace(recipe.FragmentCode) || string.IsNullOrWhiteSpace(recipe.StoneCode) ||
                !codes.Add(recipe.FragmentCode) || !codes.Add(recipe.StoneCode) ||
                _recipes.Any(other => other != recipe &&
                    other.MinimumWeaponItemLevel <= recipe.MaximumWeaponItemLevel &&
                    other.MaximumWeaponItemLevel >= recipe.MinimumWeaponItemLevel))
                throw new InvalidOperationException($"Invalid weapon breakthrough recipe: T{recipe.Tier}");
        }
    }

    public IReadOnlyList<WeaponBreakthroughRecipeOptions> Recipes => _recipes;
    public WeaponBreakthroughRecipeOptions? FindTier(int tier) => _recipes.SingleOrDefault(recipe => recipe.Tier == tier);
    public WeaponBreakthroughRecipeOptions? FindForWeapon(int itemLevel) => _recipes.SingleOrDefault(recipe =>
        itemLevel >= recipe.MinimumWeaponItemLevel && itemLevel <= recipe.MaximumWeaponItemLevel);
}
