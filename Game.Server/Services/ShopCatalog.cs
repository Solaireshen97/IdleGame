using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ShopCatalog
{
    private readonly Dictionary<string, ShopItemOptions> _items = new(StringComparer.OrdinalIgnoreCase);

    public ShopCatalog(IOptions<ShopOptions> options, ConsumableCatalog consumables, WeaponCatalog weapons)
    {
        foreach (var item in options.Value.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || item.Price is < 1 or > 1_000_000 ||
                item.Kind is not ("Consumable" or "Weapon") ||
                (item.Kind == "Consumable" ? consumables.FindItem(item.Code) is null : weapons.FindItem(item.Code) is null) ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid shop item configuration: {item.Code}");
        }
        if (_items.Count == 0) throw new InvalidOperationException("At least one shop item must be configured.");
    }

    public IReadOnlyCollection<ShopItemOptions> Items => _items.Values;

    public ShopItemOptions? Find(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;
}
