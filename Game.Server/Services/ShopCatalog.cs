using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ShopCatalog
{
    private readonly Dictionary<string, ShopItemOptions> _items = new(StringComparer.OrdinalIgnoreCase);

    public ShopCatalog(IOptions<ShopOptions> options, ConsumableCatalog consumables, WeaponCatalog weapons, PlantingCatalog? plants = null, MaterialCatalog? materials = null)
    {
        foreach (var item in options.Value.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || item.Price is < 1 or > 1_000_000 ||
                item.RequiredCount < 1 || item.MinimumCharacterLevel < 1 ||
                item.UnlockKind is not (null or "MonsterKill" or "DungeonClear") ||
                item.UnlockKind is not null && string.IsNullOrWhiteSpace(item.UnlockTargetCode) ||
                item.Kind is not ("Consumable" or "Weapon" or "Seed") ||
                (item.Kind == "Consumable" ? consumables.FindItem(item.Code) is null :
                    item.Kind == "Seed" ? plants?.FindSeed(item.Code) is not { IsRare: false } || materials?.FindItem(item.Code) is null : weapons.FindItem(item.Code) is null) ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid shop item configuration: {item.Code}");
        }
        if (_items.Count == 0) throw new InvalidOperationException("At least one shop item must be configured.");
    }

    public IReadOnlyCollection<ShopItemOptions> Items => _items.Values;

    public ShopItemOptions? Find(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;
}
