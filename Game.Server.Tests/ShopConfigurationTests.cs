using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public class ShopConfigurationTests
{
    [Fact]
    public void ConfiguredShopProductsResolveToRealItemsAndWeapons()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"));
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var consumables = new ConsumableCatalog(Options.Create(
            configuration.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!));
        var weapons = new WeaponCatalog(Options.Create(
            configuration.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
        var materials = new MaterialCatalog(Options.Create(
            configuration.GetSection(MaterialOptions.SectionName).Get<MaterialOptions>()!));

        var catalog = new ShopCatalog(Options.Create(
            configuration.GetSection(ShopOptions.SectionName).Get<ShopOptions>()!), consumables, weapons);
        var exchanges = new DungeonExchangeCatalog(Options.Create(
            configuration.GetSection(DungeonExchangeOptions.SectionName).Get<DungeonExchangeOptions>()!),
            materials, weapons);

        Assert.Equal(7, catalog.Items.Count);
        Assert.Contains(catalog.Items, item => item.Kind == "Consumable");
        Assert.Contains(catalog.Items, item => item.Kind == "Weapon");
        Assert.Equal(42, exchanges.Offers.Count);
        Assert.Equal(6, exchanges.Offers.Select(offer => weapons.FindItem(offer.WeaponCode)!.Element).Distinct().Count());
        Assert.Equal(7, exchanges.Offers.Select(offer => offer.CurrencyCode).Distinct().Count());
        Assert.All(exchanges.Offers.GroupBy(offer => offer.DungeonCode), group =>
        {
            Assert.Equal(6, group.Select(offer => weapons.FindItem(offer.WeaponCode)!.Element).Distinct().Count());
            Assert.Single(group.Select(offer => offer.CurrencyCode).Distinct());
        });
    }
}
