using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class DungeonExchangeCatalog
{
    private readonly Dictionary<string, DungeonExchangeOfferOptions> _offers =
        new(StringComparer.OrdinalIgnoreCase);

    public DungeonExchangeCatalog(IOptions<DungeonExchangeOptions> options, MaterialCatalog materials,
        WeaponCatalog weapons, SoulImprintCatalog? soulImprints = null)
    {
        foreach (var offer in options.Value.Offers)
        {
            var isWeapon = string.Equals(offer.RewardKind, "Weapon", StringComparison.OrdinalIgnoreCase);
            var isMaterial = string.Equals(offer.RewardKind, "Material", StringComparison.OrdinalIgnoreCase);
            var isSoulImprint = string.Equals(offer.RewardKind, "SoulImprint", StringComparison.OrdinalIgnoreCase);
            var rewardExists = isWeapon
                ? weapons.FindItem(offer.EffectiveRewardCode) is not null && offer.RewardQuantity == 1
                : isMaterial
                    ? materials.FindItem(offer.EffectiveRewardCode) is not null
                    : isSoulImprint && soulImprints?.Find(offer.EffectiveRewardCode) is not null &&
                      offer.RewardQuantity == 1;
            if (string.IsNullOrWhiteSpace(offer.Code) || string.IsNullOrWhiteSpace(offer.DungeonCode) ||
                string.IsNullOrWhiteSpace(offer.DungeonName) || offer.Cost <= 0 || offer.RewardQuantity <= 0 ||
                materials.FindItem(offer.CurrencyCode) is null || !rewardExists ||
                !_offers.TryAdd(offer.Code, offer))
                throw new InvalidOperationException($"Invalid dungeon exchange offer: {offer.Code}");
        }
    }

    public IReadOnlyCollection<DungeonExchangeOfferOptions> Offers => _offers.Values;

    public DungeonExchangeOfferOptions? Find(string? code) =>
        code is not null && _offers.TryGetValue(code, out var offer) ? offer : null;
}
