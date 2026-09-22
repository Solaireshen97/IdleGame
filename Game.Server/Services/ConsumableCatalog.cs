using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ConsumableCatalog
{
    private readonly Dictionary<string, ConsumableItemOptions> _items;

    public ConsumableCatalog(IOptions<ConsumableOptions> options)
    {
        var settings = options.Value;
        if (settings.Items.Count == 0) throw new InvalidOperationException("At least one consumable item must be configured.");

        _items = new Dictionary<string, ConsumableItemOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in settings.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                string.IsNullOrWhiteSpace(item.CooldownGroup) || item.HealAmount < 0 || item.CooldownRounds < 0 ||
                item.HealMaxHpPercent is < 0 or > 100 || item.HealAmount == 0 && item.HealMaxHpPercent == 0 ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid consumable item configuration: {item.Code}");
            item.CooldownGroup = item.CooldownGroup.Trim().ToLowerInvariant();
        }
    }

    public IReadOnlyCollection<ConsumableItemOptions> Items => _items.Values;

    public ConsumableItemOptions? FindItem(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;

    public static int HealAmountFor(ConsumableItemOptions item, int maxHp) =>
        RecoveryCalculator.Calculate(maxHp, item.HealAmount, item.HealMaxHpPercent);
}
