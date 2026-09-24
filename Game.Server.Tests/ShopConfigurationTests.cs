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
        var characterSlots = new CharacterSlotCatalog(Options.Create(
            configuration.GetSection(CharacterSlotOptions.SectionName).Get<CharacterSlotOptions>()!));
        var consumables = new ConsumableCatalog(Options.Create(
            configuration.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!));
        var weapons = new WeaponCatalog(Options.Create(
            configuration.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
        var materials = new MaterialCatalog(Options.Create(
            configuration.GetSection(MaterialOptions.SectionName).Get<MaterialOptions>()!));
        var soulImprints = new SoulImprintCatalog(Options.Create(
            configuration.GetSection(SoulImprintOptions.SectionName).Get<SoulImprintOptions>()!));

        var catalog = new ShopCatalog(Options.Create(
            configuration.GetSection(ShopOptions.SectionName).Get<ShopOptions>()!), consumables, weapons);
        var exchanges = new DungeonExchangeCatalog(Options.Create(
            configuration.GetSection(DungeonExchangeOptions.SectionName).Get<DungeonExchangeOptions>()!),
            materials, weapons, soulImprints);

        Assert.Equal(8, catalog.Items.Count);
        Assert.Equal(15, consumables.FindItem("northshire-battle-draught")?.AttackPercent);
        Assert.Equal(2, characterSlots.InitialSlots);
        Assert.Equal(5, characterSlots.MaximumSlots);
        Assert.Equal(new[] { 500, 1500, 4000 }, characterSlots.UnlockCosts);
        Assert.Contains(catalog.Items, item => item.Kind == "Consumable");
        Assert.Contains(catalog.Items, item => item.Kind == "Weapon");
        Assert.Equal(90, exchanges.Offers.Count);
        var weaponOffers = exchanges.Offers.Where(offer => offer.RewardKind == "Weapon").ToList();
        var fragmentOffers = exchanges.Offers.Where(offer => offer.RewardKind == "Material").ToList();
        Assert.Equal(72, weaponOffers.Count);
        Assert.Equal(12, fragmentOffers.Count);
        Assert.Equal(6, weaponOffers.Select(offer => weapons.FindItem(offer.EffectiveRewardCode)!.Element).Distinct().Count());
        Assert.Equal(12, exchanges.Offers.Select(offer => offer.CurrencyCode).Distinct().Count());
        Assert.All(exchanges.Offers.GroupBy(offer => offer.DungeonCode), group =>
        {
            Assert.Equal(6, group.Where(offer => offer.RewardKind == "Weapon")
                .Select(offer => weapons.FindItem(offer.EffectiveRewardCode)!.Element).Distinct().Count());
            Assert.Single(group, offer => offer is
                { RewardKind: "Material", RewardCode: "weapon-fragment-t1", Cost: 1 });
            Assert.Single(group.Select(offer => offer.CurrencyCode).Distinct());
        });
        var eliteDungeonCodes = soulImprints.Items.Select(item => item.DungeonCode).ToHashSet();
        Assert.All(fragmentOffers.Where(offer => !eliteDungeonCodes.Contains(offer.DungeonCode)),
            offer => Assert.Equal(3, offer.RewardQuantity));
        Assert.All(fragmentOffers.Where(offer => eliteDungeonCodes.Contains(offer.DungeonCode)),
            offer => Assert.Equal(5, offer.RewardQuantity));
        var soulOffers = exchanges.Offers.Where(offer => offer.RewardKind == "SoulImprint").ToList();
        Assert.Equal(6, soulOffers.Count);
        Assert.All(soulOffers, offer => Assert.Equal(100, offer.Cost));
        Assert.Equal(soulImprints.Items.Select(item => item.Code).Order(),
            soulOffers.Select(offer => offer.EffectiveRewardCode).Order());
    }
}
