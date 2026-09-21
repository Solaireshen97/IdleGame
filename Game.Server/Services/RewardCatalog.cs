using System.Text.Json;
using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class RewardCatalog
{
    private readonly Dictionary<string, RewardBundleOptions> _kills;
    private readonly Dictionary<string, RewardBundleOptions> _clears;
    private readonly ConsumableCatalog _consumables;
    private readonly WeaponCatalog _weapons;

    public RewardCatalog(IOptions<RewardOptions> options, ConsumableCatalog consumables, WeaponCatalog weapons)
    {
        _consumables = consumables;
        _weapons = weapons;
        _kills = Validate(options.Value.MonsterKills);
        _clears = Validate(options.Value.DungeonClears);
    }

    private Dictionary<string, RewardBundleOptions> Validate(Dictionary<string, RewardBundleOptions> bundles)
    {
        var result = new Dictionary<string, RewardBundleOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, bundle) in bundles)
        {
            if (string.IsNullOrWhiteSpace(code) || bundle is null || bundle.Gold < 0 || bundle.Experience < 0 ||
                bundle.Drops.Any(drop => drop.Quantity <= 0 || drop.ChancePercent is < 0 or > 100 ||
                    drop.Kind is not ("Consumable" or "Weapon") ||
                    (drop.Kind == "Consumable" ? _consumables.FindItem(drop.Code) is null : _weapons.FindItem(drop.Code) is null)))
                throw new InvalidOperationException($"Invalid rewards for dungeon: {code}");
            result.Add(code, bundle);
        }
        return result;
    }

    public IReadOnlyList<RewardEntry> Roll(string dungeonCode, bool isClear, int roomId, int sequence,
        string eventKey, int userId, int characterId)
    {
        var bundles = isClear ? _clears : _kills;
        if (!bundles.TryGetValue(dungeonCode, out var bundle)) return [];
        var entries = new List<RewardEntry>();
        if (bundle.Gold > 0) entries.Add(NewEntry("Gold", "", bundle.Gold));
        if (bundle.Experience > 0) entries.Add(NewEntry("Experience", "", bundle.Experience));
        foreach (var drop in bundle.Drops)
        {
            if (drop.ChancePercent <= 0 || drop.ChancePercent < 100 &&
                (decimal)Random.Shared.NextDouble() * 100 >= drop.ChancePercent) continue;
            var entry = NewEntry(drop.Kind, drop.Code, drop.Quantity);
            if (drop.Kind == "Weapon")
                entry.WeaponSnapshotJson = JsonSerializer.Serialize(_weapons.CreateRewardSnapshot(drop.Code));
            entries.Add(entry);
        }
        return entries;

        RewardEntry NewEntry(string kind, string code, int quantity) => new()
        {
            RoomId = roomId, Sequence = sequence, EventKey = eventKey, UserId = userId,
            CharacterId = characterId, Kind = kind, Code = code, Quantity = quantity
        };
    }

    public string Describe(RewardEntry entry) => entry.Kind switch
    {
        "Gold" => "金币",
        "Experience" => "经验",
        "Consumable" => _consumables.FindItem(entry.Code)?.Name ?? entry.Code,
        "Weapon" => _weapons.FindItem(entry.Code)?.Name ?? entry.Code,
        _ => entry.Code
    };
}

public sealed record WeaponRewardSnapshot(string Code, string Name, ElementType Element, int Attack, int MaxHp,
    List<WeaponRewardSkillSnapshot> Skills)
{
    public CharacterWeapon ToCharacterWeapon(int characterId) => new()
    {
        CharacterId = characterId, WeaponCode = Code, Name = Name, Element = Element,
        Attack = Attack, MaxHp = MaxHp, Skills = Skills.Select((skill, index) => new CharacterWeaponSkill
        {
            SlotIndex = index + 1, SkillCode = skill.Code, Level = skill.Level
        }).ToList()
    };
}

public sealed record WeaponRewardSkillSnapshot(string Code, int Level);
