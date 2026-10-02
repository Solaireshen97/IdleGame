using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class InventoryCatalogTests
{
    [Fact]
    public void EveryConfiguredStackResolvesWithoutLosingRetiredGatheringHerbs()
    {
        var content = new Content();
        var catalog = content.CreateCatalog();
        var codes = content.Materials.Items.Select(item => item.Code)
            .Concat(content.Consumables.Items.Select(item => item.Code))
            .Concat(content.Planting.Plants.SelectMany(plant => new[] { plant.SeedCode, plant.MaterialCode }))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.All(codes, code =>
        {
            var item = catalog.DescribeStack(code, new Character());
            Assert.True(item.IsDefinitionKnown, code);
            Assert.Equal(code, item.Code);
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
            Assert.Equal(InventoryKinds.Stack, item.AssetKind);
            Assert.Equal(("", 0, 0), (item.Key, item.Quantity, item.Version));
            if (item.Category == InventoryCategories.Other)
            {
                var material = content.Materials.FindItem(code);
                Assert.NotNull(material);
                Assert.Equal(material.Name, item.Name);
                Assert.Equal(material.Description, item.Description);
                Assert.Empty(item.Usages);
            }
        });
        foreach (var plant in content.Planting.Plants)
            foreach (var code in new[] { plant.SeedCode, plant.MaterialCode })
                Assert.Equal(InventoryCategories.Planting, catalog.DescribeStack(code, new Character()).Category);
        foreach (var consumable in content.Consumables.Items)
            Assert.Equal(InventoryCategories.Supplies, catalog.DescribeStack(consumable.Code, new Character()).Category);
        foreach (var recipe in content.Production.Recipes)
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                var item = catalog.DescribeStack(ingredient.Code, new Character());
                Assert.NotEqual(InventoryCategories.Other, item.Category);
                Assert.Contains("炼金原料", item.Tags);
                Assert.Contains(item.Usages, usage => usage.Route == $"/production?itemCode={ingredient.Code}");
            }
            var result = catalog.DescribeStack(recipe.OutputCode, new Character());
            Assert.Equal(InventoryCategories.Supplies, result.Category);
            Assert.Contains(result.Usages, usage => usage.Route == $"/production?itemCode={recipe.OutputCode}");
        }
        foreach (var code in new[] { "tirisfal-gravemoss", "durotar-aloe", "dun-morogh-frostdew", "mulgore-sage", "eversong-goldleaf" })
        {
            var item = catalog.DescribeStack(code, new Character());
            Assert.Equal(InventoryCategories.Planting, item.Category);
            Assert.Contains("草药", item.Tags);
        }
        var legacyHerb = catalog.DescribeStack("briarthorn", new Character());
        var isIngredient = content.Production.Recipes.Any(recipe => recipe.Ingredients.Any(ingredient =>
            string.Equals(ingredient.Code, "briarthorn", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(isIngredient ? InventoryCategories.Planting : InventoryCategories.Other, legacyHerb.Category);
        Assert.Equal(isIngredient, legacyHerb.Tags.Contains("草药"));
        Assert.Equal(isIngredient, legacyHerb.Usages.Any(usage => usage.Route == "/production?itemCode=briarthorn"));
    }

    [Fact]
    public void BriarthornRemainsDefinedWithoutInventedUsagesAfterItsRecipesAreRemoved()
    {
        var content = new Content(production: options => options.Recipes.RemoveAll(recipe =>
            recipe.Ingredients.Any(ingredient =>
                string.Equals(ingredient.Code, "briarthorn", StringComparison.OrdinalIgnoreCase))));
        Assert.DoesNotContain(content.Production.Recipes, recipe => recipe.Ingredients.Any(ingredient =>
            string.Equals(ingredient.Code, "briarthorn", StringComparison.OrdinalIgnoreCase)));
        var material = content.Materials.FindItem("briarthorn");
        Assert.NotNull(material);
        var item = content.CreateCatalog().DescribeStack("briarthorn", new Character());
        Assert.True(item.IsDefinitionKnown);
        Assert.Equal(("briarthorn", material.Name, material.Description), (item.Code, item.Name, item.Description));
        Assert.Equal(InventoryCategories.Other, item.Category);
        Assert.Empty(item.Tags);
        Assert.Empty(item.Usages);
        // Definitions leave ownership fields to the inventory query even when a purpose is retired.
        Assert.Equal(("", 0, 0), (item.Key, item.Quantity, item.Version));
    }

    [Fact]
    public void SeedAndHerbClassificationUsesRelationshipsRatherThanWordsInCode()
    {
        var catalog = new Content().CreateCatalog();
        var herb = catalog.DescribeStack("mulgore-stormseed", new Character());
        var seed = catalog.DescribeStack("seed-mulgore-stormseed", new Character());
        Assert.Equal(InventoryCategories.Planting, herb.Category);
        Assert.Contains("草药", herb.Tags);
        Assert.DoesNotContain("种子", herb.Tags);
        Assert.Contains("稀有", herb.Tags);
        Assert.Contains("种子", seed.Tags);
        Assert.DoesNotContain("草药", seed.Tags);
        Assert.Contains(seed.Usages, usage => usage.Route == "/gathering?plantCode=mulgore-stormseed");
        Assert.Contains(herb.Usages, usage => usage.Route == "/production?itemCode=mulgore-stormseed");
    }

    [Fact]
    public void BreakthroughAndExchangeClassificationsFollowTheirCatalogs()
    {
        var content = new Content();
        var catalog = content.CreateCatalog();
        foreach (var recipe in content.Breakthroughs.Recipes)
            foreach (var code in new[] { recipe.FragmentCode, recipe.StoneCode })
            {
                var item = catalog.DescribeStack(code, new Character());
                Assert.Equal(InventoryCategories.Upgrade, item.Category);
                Assert.Equal(recipe.Tier, item.Tier);
                Assert.Contains(item.Usages, usage => usage.Route == "/inventory?category=upgrade");
            }
        foreach (var code in content.Exchanges.Offers.Select(offer => offer.CurrencyCode).Distinct())
        {
            var item = catalog.DescribeStack(code, new Character());
            Assert.Equal(InventoryCategories.Exchange, item.Category);
            Assert.Contains(item.Usages, usage => usage.Route == $"/shop?itemCode={code}");
            Assert.Single(item.Usages);
        }
        // An exchange reward is classified by its purpose, not its acquisition channel.
        Assert.Equal(InventoryCategories.Upgrade, catalog.DescribeStack("weapon-fragment-t1", new Character()).Category);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(21)]
    [InlineData(31)]
    public void ConsumableDescriptionsUseCharacterLevelAndExistingEffectRules(int level)
    {
        var content = new Content();
        var catalog = content.CreateCatalog();
        foreach (var consumable in content.Consumables.Items)
        {
            var item = catalog.DescribeStack(consumable.Code, new Character { Level = level });
            Assert.Equal(ConsumableCatalog.Description(consumable, level), item.Description);
            Assert.Equal(InventoryCategories.Supplies, item.Category);
            Assert.Equal(consumable.Tier, item.Tier);
        }
        Assert.Equal(3, content.Consumables.Items.Select(item => item.Kind).Distinct().Count());
        if (level == 31)
            Assert.Equal("当前等级已无效果", catalog.DescribeStack("minor-healing-potion", new Character { Level = level }).Description);
    }

    [Theory]
    [InlineData("weapon-fragment-t2", 2)]
    [InlineData("WEAPON-FRAGMENT-T99", 99)]
    [InlineData("weapon-fragment-t2147483647", int.MaxValue)]
    public void ValidFutureFragmentTiersAreKnownWithoutChangingExistingWeaponRules(string code, int tier)
    {
        var item = new Content().CreateCatalog().DescribeStack(code, new Character());
        Assert.True(item.IsDefinitionKnown);
        Assert.Equal(code, item.Code);
        Assert.Equal(WeaponRules.FragmentName(tier), item.Name);
        Assert.Equal(tier, item.Tier);
        Assert.Equal(InventoryCategories.Upgrade, item.Category);
    }

    [Theory]
    [InlineData("retired-mystery")]
    [InlineData("seed-invented-herb")]
    [InlineData("weapon-fragment-t0")]
    [InlineData("weapon-fragment-t01")]
    [InlineData("weapon-fragment-t+1")]
    [InlineData("weapon-fragment-t2147483648")]
    [InlineData("weapon-breakthrough-stone-t99")]
    public void UnknownOrMalformedCodesArePreservedWithoutInventedDefinitions(string code)
    {
        var item = new Content().CreateCatalog().DescribeStack(code, new Character());
        Assert.Equal(code, item.Code);
        Assert.Equal(code, item.Name);
        Assert.False(item.IsDefinitionKnown);
        Assert.Equal(InventoryCategories.Other, item.Category);
        Assert.Equal("", item.Description);
        Assert.Null(item.Tier);
        Assert.Empty(item.Tags);
        Assert.Empty(item.Usages);
    }

    [Fact]
    public void MaterialConsumableOverlapIsCompatibleAndLookupPreservesStoredCode()
    {
        var content = new Content(materials => materials.Items.Add(new MaterialItemOptions
        {
            Code = "MINOR-HEALING-POTION", Name = "旧药剂名称", Description = "旧药剂描述"
        }));
        var catalog = content.CreateCatalog();
        var character = new Character { Id = 5, Level = 11, Gold = 91, Version = 7, Hp = 5, MaxHp = 100 };
        var item = catalog.DescribeStack("MiNoR-HeAlInG-PoTiOn", character);
        Assert.Equal("MiNoR-HeAlInG-PoTiOn", item.Code);
        Assert.Equal(content.Consumables.FindItem(item.Code)!.Name, item.Name);
        Assert.Equal(InventoryCategories.Supplies, item.Category);
        Assert.Equal(ConsumableCatalog.Description(content.Consumables.FindItem(item.Code)!, 11), item.Description);
        Assert.Equal((5, 11, 91, 7, 5, 100), (character.Id, character.Level, character.Gold, character.Version, character.Hp, character.MaxHp));
        item.Tags.Clear();
        item.Usages[0].Route = "changed";
        var next = catalog.DescribeStack(item.Code, character);
        Assert.NotEmpty(next.Tags);
        Assert.Contains(next.Usages, usage => usage.Route == "/formations?section=consumables&itemCode=minor-healing-potion");
    }

    [Fact]
    public void ContradictoryGameplayRelationshipsFailInsteadOfSilentlyReclassifying()
    {
        var currencyAsSeed = new Content(planting: options => options.Plants[0].SeedCode = "deep-mine-token");
        Assert.Contains("deep-mine-token", Assert.Throws<InvalidOperationException>(currencyAsSeed.CreateCatalog).Message);
        var herbAsSeed = new Content(planting: options => options.Plants[1].SeedCode = "peacebloom");
        Assert.Contains("peacebloom", Assert.Throws<InvalidOperationException>(herbAsSeed.CreateCatalog).Message);
    }

    [Fact]
    public void ExplicitDefinitionsSupportUnconventionalSeedAndBreakthroughCodes()
    {
        var content = new Content(planting: options => options.Plants[0].SeedCode = "garden-starter",
            breakthroughs: options => options.Recipes.Add(new WeaponBreakthroughRecipeOptions
            {
                Tier = 2, FragmentCode = "crystal-dust", StoneCode = "cloud-step-crystal",
                FragmentsPerStone = 40, MinimumWeaponItemLevel = 11, MaximumWeaponItemLevel = 20
            }));
        var catalog = content.CreateCatalog();
        var seed = catalog.DescribeStack("garden-starter", new Character());
        Assert.True(seed.IsDefinitionKnown);
        Assert.Equal("宁神花种子", seed.Name);
        Assert.Contains("种子", seed.Tags);
        foreach (var code in new[] { "crystal-dust", "cloud-step-crystal" })
        {
            var item = catalog.DescribeStack(code, new Character());
            Assert.True(item.IsDefinitionKnown);
            Assert.Equal(InventoryCategories.Upgrade, item.Category);
            Assert.Equal(2, item.Tier);
        }
    }

    [Fact]
    public void DefinedMaterialWithoutGameplayRelationshipsRemainsKnownInOtherCategory()
    {
        var content = new Content(materials => materials.Items.Add(new MaterialItemOptions
        {
            Code = "archived-material", Name = "旧纪念物", Description = "保留的历史道具。"
        }));
        var item = content.CreateCatalog().DescribeStack("archived-material", new Character());
        Assert.True(item.IsDefinitionKnown);
        Assert.Equal("旧纪念物", item.Name);
        Assert.Equal(InventoryCategories.Other, item.Category);
        Assert.Empty(item.Usages);
    }

    [Fact]
    public void AdditionalRecipeUsageDoesNotReplaceAnExistingUpgradeCategory()
    {
        var content = new Content(production: options => options.Recipes[0].Ingredients.Add(
            new ProductionIngredientOptions { Code = "weapon-fragment-t1", Quantity = 1 }));
        var item = content.CreateCatalog().DescribeStack("weapon-fragment-t1", new Character());
        Assert.Equal(InventoryCategories.Upgrade, item.Category);
        Assert.Contains("炼金原料", item.Tags);
        Assert.DoesNotContain("草药", item.Tags);
        Assert.Contains(item.Usages, usage => usage.Route == "/production?itemCode=weapon-fragment-t1");
        Assert.Contains(item.Usages, usage => usage.Route == "/inventory?category=weapons");
    }

    [Fact]
    public void RewardNamesUseAssetKindAndKeepUnknownCodes()
    {
        var content = new Content();
        var catalog = content.CreateCatalog();
        Assert.Equal("金币", catalog.DescribeReward("Gold", ""));
        Assert.Equal("经验", catalog.DescribeReward("Experience", ""));
        Assert.Equal(content.Weapons.FindItem("goldtooth-pickaxe")!.Name, catalog.DescribeReward("Weapon", "goldtooth-pickaxe"));
        var soul = content.Souls.Items.First();
        Assert.Equal(soul.Name, catalog.DescribeReward("SoulImprint", soul.Code));
        Assert.Equal(WeaponRules.FragmentName(2), catalog.DescribeReward("Material", "weapon-fragment-t2"));
        Assert.Equal("unknown-soul", catalog.DescribeReward("SoulImprint", "unknown-soul"));
        Assert.Equal("minor-healing-potion", catalog.DescribeReward("Weapon", "minor-healing-potion"));
    }

    private sealed class Content
    {
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        public MaterialCatalog Materials { get; }
        public ConsumableCatalog Consumables { get; }
        public PlantingCatalog Planting { get; }
        public WeaponCatalog Weapons { get; }
        public WeaponBreakthroughCatalog Breakthroughs { get; }
        public SoulImprintCatalog Souls { get; }
        public DungeonExchangeCatalog Exchanges { get; }
        public ProductionCatalog Production { get; }
        private GatheringCatalog Gathering { get; }

        public Content(Action<MaterialOptions>? materials = null, Action<PlantingOptions>? planting = null,
            Action<WeaponBreakthroughOptions>? breakthroughs = null, Action<ProductionOptions>? production = null)
        {
            var materialOptions = Bind<MaterialOptions>(MaterialOptions.SectionName);
            materials?.Invoke(materialOptions.Value);
            Materials = new(materialOptions);
            Weapons = new(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Consumables = new(Bind<ConsumableOptions>(ConsumableOptions.SectionName), Weapons);
            var plantingOptions = Bind<PlantingOptions>(PlantingOptions.SectionName);
            planting?.Invoke(plantingOptions.Value);
            Planting = new(plantingOptions);
            var breakthroughOptions = Bind<WeaponBreakthroughOptions>(WeaponBreakthroughOptions.SectionName);
            breakthroughs?.Invoke(breakthroughOptions.Value);
            Breakthroughs = new(breakthroughOptions);
            Souls = new(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
            Exchanges = new(Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), Materials, Weapons, Souls);
            var world = WorldCatalog.LoadDefault();
            var productionOptions = Bind<ProductionOptions>(ProductionOptions.SectionName);
            production?.Invoke(productionOptions.Value);
            Production = new(productionOptions, world, Materials, Consumables);
            Gathering = new(Bind<GatheringOptions>(GatheringOptions.SectionName), world, Materials);
        }

        private IOptions<T> Bind<T>(string section) where T : class, new() =>
            Options.Create(_configuration.GetSection(section).Get<T>()!);

        public ItemDefinitionCatalog CreateCatalog() => new(Materials, Consumables, Planting, Weapons,
            Breakthroughs, Souls, Exchanges, Production, Gathering);
    }
}
