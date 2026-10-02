using System.Globalization;
using Game.Shared;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Display definitions only; ownership, quantities and instance state belong to inventory queries.</summary>
public sealed class ItemDefinitionCatalog
{
    private readonly Dictionary<string, StackDefinition> _stacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConsumableCatalog _consumables;
    private readonly WeaponCatalog _weapons;
    private readonly SoulImprintCatalog _souls;

    public ItemDefinitionCatalog(MaterialCatalog materials, ConsumableCatalog consumables,
        PlantingCatalog planting, WeaponCatalog weapons, WeaponBreakthroughCatalog breakthroughs,
        SoulImprintCatalog souls, DungeonExchangeCatalog exchanges, ProductionCatalog production,
        GatheringCatalog? gathering = null)
    {
        _consumables = consumables;
        _weapons = weapons;
        _souls = souls;
        foreach (var item in materials.Items)
            _stacks.Add(item.Code, new(item.Name, item.Description));

        // A material definition may also carry a consumable's display text. This is
        // compatible overlap, while contradictory gameplay classifications are rejected below.
        foreach (var item in consumables.Items)
        {
            var definition = GetOrAdd(item.Code, item.Name, "");
            definition.Name = item.Name;
            SetCategory(item.Code, definition, InventoryCategories.Supplies);
            definition.Tier = item.Tier;
            definition.Tags.Add(item.Kind switch
            {
                "Healing" => "治疗药水", "CombatBuff" => "强化药水", _ => "合剂"
            });
            AddUsage(definition, "编队配置补给", "/formations?section=consumables", "itemCode", item.Code);
        }

        foreach (var plant in planting.Plants)
        {
            var seed = GetOrAdd(plant.SeedCode, $"{plant.Name}种子", $"播种后可收获{plant.Name}。");
            SetCategory(plant.SeedCode, seed, InventoryCategories.Planting);
            if (seed.Tags.Contains("草药"))
                throw new InvalidOperationException($"Conflicting seed and herb definitions: {plant.SeedCode}");
            seed.Tags.Add("种子");
            seed.Tags.Add(plant.IsRare ? "稀有" : "普通");
            AddUsage(seed, "前往播种", "/gathering", "plantCode", plant.Code);
            var herb = GetOrAdd(plant.MaterialCode, plant.Name, "种植收获的草药。");
            SetCategory(plant.MaterialCode, herb, InventoryCategories.Planting);
            if (herb.Tags.Contains("种子"))
                throw new InvalidOperationException($"Conflicting seed and herb definitions: {plant.MaterialCode}");
            herb.Tags.Add("草药");
            herb.Tags.Add(plant.IsRare ? "稀有" : "普通");
        }

        if (gathering is not null)
            foreach (var point in gathering.Points)
            {
                AddGatheredHerb(point.MaterialCode, point.IsRare);
                if (point.BonusMaterialCode is { } bonusCode) AddGatheredHerb(bonusCode, false);
            }

        foreach (var recipe in breakthroughs.Recipes)
        {
            AddBreakthrough(recipe.FragmentCode, $"T{recipe.Tier} 通用突破碎片", "突破石合成材料。", recipe.Tier, "突破碎片");
            AddBreakthrough(recipe.StoneCode, $"T{recipe.Tier} 通用突破石", "用于武器突破。", recipe.Tier, "突破石");
        }

        foreach (var offer in exchanges.Offers)
        {
            var currency = _stacks[offer.CurrencyCode];
            SetCategory(offer.CurrencyCode, currency, InventoryCategories.Exchange);
            currency.Tags.Add("副本徽记");
            AddUsage(currency, "前往兑换", "/shop", "itemCode", offer.CurrencyCode);
        }

        foreach (var (code, definition) in _stacks)
            if (TryFragmentTier(code, out var tier))
            {
                SetCategory(code, definition, InventoryCategories.Upgrade);
                definition.Tier = tier;
                definition.Tags.Add("武器碎片");
                AddUsage(definition, "强化武器", "/inventory", "category", InventoryCategories.Weapons);
            }

        foreach (var recipe in production.Recipes)
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                var definition = _stacks[ingredient.Code];
                // Existing upgrade/exchange/supply definitions keep their primary category
                // if a future recipe also uses them as ingredients.
                if (definition.Category == InventoryCategories.Other)
                {
                    definition.Category = InventoryCategories.Planting;
                    definition.Tags.Add("草药");
                }
                definition.Tags.Add("炼金原料");
                AddUsage(definition, "前往炼金", "/production", "itemCode", ingredient.Code);
            }
            AddUsage(_stacks[recipe.OutputCode], "前往炼金", "/production", "itemCode", recipe.OutputCode);
        }

    }

    public InventoryEntryDto DescribeStack(string code, Character character)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(character);
        var definition = FindStack(code);
        return new InventoryEntryDto
        {
            AssetKind = InventoryKinds.Stack,
            Code = code,
            Name = definition?.Name ?? code,
            Description = _consumables.FindItem(code) is { } consumable
                ? ConsumableCatalog.Description(consumable, character.Level) : definition?.Description ?? "",
            Category = definition?.Category ?? InventoryCategories.Other,
            IsDefinitionKnown = definition is not null,
            Tier = definition?.Tier,
            Tags = definition?.Tags.Order(StringComparer.Ordinal).ToList() ?? [],
            Usages = definition?.Usages.Select(usage => new InventoryUsageDto
                { Label = usage.Label, Route = usage.Route }).ToList() ?? []
        };
    }

    public string DescribeReward(string kind, string code) => kind.ToLowerInvariant() switch
    {
        "gold" => "金币",
        "experience" => "经验",
        "weapon" => _weapons.FindItem(code)?.Name ?? code,
        "soulimprint" or "soul" => _souls.Find(code)?.Name ?? code,
        "consumable" or "material" or "stack" => FindStack(code)?.Name ?? code,
        _ => code
    };

    private StackDefinition? FindStack(string code)
    {
        if (_stacks.TryGetValue(code, out var definition)) return definition;
        if (!TryFragmentTier(code, out var tier)) return null;
        definition = new(WeaponRules.FragmentName(tier),
            $"用于强化 {(long)(tier - 1) * 10 + 1}～{(long)tier * 10} 级武器的通用材料。")
        {
            Category = InventoryCategories.Upgrade, Tier = tier
        };
        definition.Tags.Add("武器碎片");
        AddUsage(definition, "强化武器", "/inventory", "category", InventoryCategories.Weapons);
        return definition;
    }

    private static bool TryFragmentTier(string code, out int tier)
    {
        const string prefix = "weapon-fragment-t";
        tier = 0;
        return code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(code.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out tier) &&
            tier > 0 && string.Equals(code, WeaponRules.FragmentCode(tier), StringComparison.OrdinalIgnoreCase);
    }

    private StackDefinition GetOrAdd(string code, string name, string description)
    {
        if (_stacks.TryGetValue(code, out var definition)) return definition;
        _stacks.Add(code, definition = new(name, description));
        return definition;
    }

    private void AddGatheredHerb(string code, bool rare)
    {
        var definition = _stacks[code];
        SetCategory(code, definition, InventoryCategories.Planting);
        if (definition.Tags.Contains("种子"))
            throw new InvalidOperationException($"Conflicting seed and herb definitions: {code}");
        definition.Tags.Add("草药");
        definition.Tags.Add(rare ? "稀有" : "普通");
    }

    private void AddBreakthrough(string code, string name, string description, int tier, string tag)
    {
        var definition = GetOrAdd(code, name, description);
        SetCategory(code, definition, InventoryCategories.Upgrade);
        definition.Tier = tier;
        definition.Tags.Add(tag);
        AddUsage(definition, "武器突破与合成", "/inventory", "category", InventoryCategories.Upgrade);
    }

    private static void SetCategory(string code, StackDefinition definition, string category)
    {
        if (definition.Category != InventoryCategories.Other && definition.Category != category)
            throw new InvalidOperationException($"Conflicting inventory categories for {code}: {definition.Category}, {category}");
        definition.Category = category;
    }

    private static void AddUsage(StackDefinition definition, string label, string route, string parameter, string value)
    {
        var target = $"{route}{(route.Contains('?') ? "&" : "?")}{parameter}={Uri.EscapeDataString(value)}";
        if (definition.Usages.All(usage => usage.Route != target)) definition.Usages.Add(new(label, target));
    }

    private sealed class StackDefinition(string name, string description)
    {
        public string Name { get; set; } = name;
        public string Description { get; } = description;
        public string Category { get; set; } = InventoryCategories.Other;
        public int? Tier { get; set; }
        public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);
        public List<(string Label, string Route)> Usages { get; } = [];
    }
}
