using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class MaterialCatalog
{
    private readonly Dictionary<string, MaterialItemOptions> _items = new(StringComparer.OrdinalIgnoreCase);

    public MaterialCatalog(IOptions<MaterialOptions> options)
    {
        foreach (var item in options.Value.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                string.IsNullOrWhiteSpace(item.Description) || !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid material configuration: {item.Code}");
        }
    }

    public IReadOnlyCollection<MaterialItemOptions> Items => _items.Values;

    public MaterialItemOptions? FindItem(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;
}
