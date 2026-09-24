using Game.Server.Configuration;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class SoulImprintCatalog
{
    private readonly Dictionary<string, SoulImprintDefinitionOptions> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public SoulImprintCatalog(IOptions<SoulImprintOptions> options)
    {
        foreach (var item in options.Value.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                string.IsNullOrWhiteSpace(item.Description) || string.IsNullOrWhiteSpace(item.DungeonCode) ||
                item.Tier <= 0 || !Enum.IsDefined(item.Element) || !Enum.IsDefined(item.EffectType) ||
                item.PowerPercent < 0 || item.SecondaryPowerPercent < 0 || item.DurationRounds < 0 ||
                item.InitialCooldownRounds < 0 || item.CooldownRounds <= 0 || item.DismantleFragments <= 0 ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid soul imprint configuration: {item.Code}");
        }
    }

    public IReadOnlyCollection<SoulImprintDefinitionOptions> Items => _items.Values;

    public SoulImprintDefinitionOptions? Find(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;

    public CharacterSoulImprint Materialize(string code, int characterId) =>
        Find(code) is not null
            ? new CharacterSoulImprint { CharacterId = characterId, SoulImprintCode = code }
            : throw new InvalidOperationException($"Unknown soul imprint: {code}");
}
