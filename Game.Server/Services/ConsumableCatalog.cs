using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ConsumableCatalog
{
    private readonly Dictionary<string, ConsumableItemOptions> _items;
    private readonly Dictionary<string, IReadOnlyList<ConsumableDropOptions>> _drops;

    public ConsumableCatalog(IOptions<ConsumableOptions> options)
    {
        var settings = options.Value;
        if (settings.Items.Count == 0) throw new InvalidOperationException("At least one consumable item must be configured.");

        _items = new Dictionary<string, ConsumableItemOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in settings.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                string.IsNullOrWhiteSpace(item.CooldownGroup) || item.HealAmount <= 0 || item.CooldownRounds < 0 ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid consumable item configuration: {item.Code}");
            item.CooldownGroup = item.CooldownGroup.Trim().ToLowerInvariant();
        }

        _drops = new Dictionary<string, IReadOnlyList<ConsumableDropOptions>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dungeonCode, drops) in settings.DungeonVictoryDrops)
        {
            if (string.IsNullOrWhiteSpace(dungeonCode) ||
                drops.Any(drop => !_items.ContainsKey(drop.ItemCode) || drop.Quantity <= 0) ||
                drops.Select(drop => drop.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != drops.Count)
                throw new InvalidOperationException($"Invalid consumable drops for dungeon: {dungeonCode}");
            _drops.Add(dungeonCode, drops);
        }
    }

    public IReadOnlyCollection<ConsumableItemOptions> Items => _items.Values;

    public ConsumableItemOptions? FindItem(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;

    public IReadOnlyList<ConsumableDropOptions> GetVictoryDrops(string dungeonCode) =>
        _drops.TryGetValue(dungeonCode, out var drops) ? drops : [];
}
