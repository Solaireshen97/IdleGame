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

        var catalog = new ShopCatalog(Options.Create(
            configuration.GetSection(ShopOptions.SectionName).Get<ShopOptions>()!), consumables, weapons);

        Assert.Equal(7, catalog.Items.Count);
        Assert.Contains(catalog.Items, item => item.Kind == "Consumable");
        Assert.Contains(catalog.Items, item => item.Kind == "Weapon");
    }
}
