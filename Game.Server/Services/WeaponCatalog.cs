using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class WeaponCatalog
{
    private readonly Dictionary<string, WeaponTemplateOptions> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _starterPacks = new(StringComparer.OrdinalIgnoreCase);

    public WeaponCatalog(IOptions<WeaponOptions> options)
    {
        foreach (var item in options.Value.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                !Enum.IsDefined(item.Element) || item.Attack < 0 || item.MaxHp <= 0 ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid weapon configuration: {item.Code}");
        }
        foreach (var (profession, codes) in options.Value.StarterPacks)
        {
            if (string.IsNullOrWhiteSpace(profession) || codes.Count == 0 ||
                codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count ||
                codes.Any(code => !_items.ContainsKey(code)) || !_starterPacks.TryAdd(profession, codes))
                throw new InvalidOperationException($"Invalid starter weapons for: {profession}");
        }
        if (_items.Count == 0 || _starterPacks.Count == 0)
            throw new InvalidOperationException("Weapons and starter packs must be configured.");
    }

    public IReadOnlyList<CharacterWeapon> CreateStarterWeapons(int characterId, string professionCode)
    {
        if (!_starterPacks.TryGetValue(professionCode, out var codes))
            throw new InvalidOperationException($"Missing starter weapons for: {professionCode}");
        return codes.Select((code, index) =>
        {
            var item = _items[code];
            return new CharacterWeapon
            {
                CharacterId = characterId,
                WeaponCode = item.Code,
                Name = item.Name,
                Element = item.Element,
                Attack = item.Attack,
                MaxHp = item.MaxHp,
                EquippedSlotIndex = index == 0 ? 1 : null
            };
        }).ToList();
    }
}
