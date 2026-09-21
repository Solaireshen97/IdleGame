using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class DungeonExchangeCatalog
{
    private readonly Dictionary<string, DungeonExchangeOfferOptions> _offers =
        new(StringComparer.OrdinalIgnoreCase);

    public DungeonExchangeCatalog(IOptions<DungeonExchangeOptions> options, MaterialCatalog materials,
        WeaponCatalog weapons)
    {
        foreach (var offer in options.Value.Offers)
        {
            if (string.IsNullOrWhiteSpace(offer.Code) || string.IsNullOrWhiteSpace(offer.DungeonCode) ||
                string.IsNullOrWhiteSpace(offer.DungeonName) || offer.Cost <= 0 ||
                materials.FindItem(offer.CurrencyCode) is null || weapons.FindItem(offer.WeaponCode) is null ||
                !_offers.TryAdd(offer.Code, offer))
                throw new InvalidOperationException($"Invalid dungeon exchange offer: {offer.Code}");
        }
    }

    public IReadOnlyCollection<DungeonExchangeOfferOptions> Offers => _offers.Values;

    public DungeonExchangeOfferOptions? Find(string? code) =>
        code is not null && _offers.TryGetValue(code, out var offer) ? offer : null;
}
