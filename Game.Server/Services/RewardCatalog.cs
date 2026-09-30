using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Server.Configuration;
using Game.Shared;
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
    private readonly MaterialCatalog? _materials;
    private readonly SoulImprintCatalog? _soulImprints;
    private readonly bool _grantFirstHuntWeapon;
    private readonly Random _random;

    public RewardCatalog(IOptions<RewardOptions> options, ConsumableCatalog consumables, WeaponCatalog weapons,
        MaterialCatalog? materials = null, SoulImprintCatalog? soulImprints = null, Random? random = null)
    {
        _consumables = consumables;
        _weapons = weapons;
        _materials = materials;
        _soulImprints = soulImprints;
        _grantFirstHuntWeapon = options.Value.GrantFirstHuntWeapon;
        _random = random ?? Random.Shared;
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
                    drop.Kind is not ("Consumable" or "Weapon" or "Material" or "SoulImprint") ||
                    drop.Kind switch
                    {
                        "Consumable" => _consumables.FindItem(drop.Code) is null,
                        "Weapon" => _weapons.FindItem(drop.Code) is null,
                        "Material" => _materials?.FindItem(drop.Code) is null,
                        "SoulImprint" => _soulImprints?.Find(drop.Code) is null,
                        _ => true
                    }))
                throw new InvalidOperationException($"Invalid rewards for dungeon: {code}");
            result.Add(code, bundle);
        }
        return result;
    }

    public DungeonRewardRules CaptureRules(string dungeonCode, IEnumerable<string> killCodes)
    {
        var rules = new DungeonRewardRules { FirstHuntWeapon = FirstHuntWeapon(dungeonCode) };
        foreach (var code in killCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            if (_kills.TryGetValue(code, out var bundle)) rules.Kills.Add(code, bundle);
        foreach (var code in new[] { dungeonCode, $"{dungeonCode}-first-clear" })
            if (_clears.TryGetValue(code, out var bundle)) rules.Clears.Add(code, bundle);
        foreach (var code in rules.Kills.Values.Concat(rules.Clears.Values).SelectMany(bundle => bundle.Drops)
                     .Where(drop => drop.Kind == "Weapon").Select(drop => drop.Code).Distinct(StringComparer.OrdinalIgnoreCase))
            rules.Weapons.Add(code, _weapons.CreateRewardSnapshot(code));
        return JsonSerializer.Deserialize<DungeonRewardRules>(JsonSerializer.Serialize(rules))!;
    }

    public IReadOnlyList<RewardEntry> Roll(string dungeonCode, bool isClear, int roomId, int sequence,
        string eventKey, int userId, int characterId, DungeonRewardRules? frozen = null)
    {
        var bundles = frozen is null ? isClear ? _clears : _kills : isClear ? frozen.Clears : frozen.Kills;
        if (!bundles.TryGetValue(dungeonCode, out var bundle)) return [];
        var entries = new List<RewardEntry>();
        if (bundle.Gold > 0) entries.Add(NewEntry("Gold", "", bundle.Gold));
        if (bundle.Experience > 0) entries.Add(NewEntry("Experience", "", bundle.Experience));
        foreach (var drop in bundle.Drops)
        {
            if (drop.ChancePercent <= 0 || drop.ChancePercent < 100 &&
                (decimal)_random.NextDouble() * 100 >= drop.ChancePercent) continue;
            if (drop.Kind == "Weapon")
            {
                for (var index = 0; index < drop.Quantity; index++)
                {
                    var entry = NewEntry(drop.Kind, drop.Code, 1);
                    entry.WeaponSnapshotJson = JsonSerializer.Serialize(frozen is null
                        ? _weapons.CreateRewardSnapshot(drop.Code) : frozen.Weapons[drop.Code]);
                    entries.Add(entry);
                }
            }
            else entries.Add(NewEntry(drop.Kind, drop.Code, drop.Quantity));
        }
        return entries;

        RewardEntry NewEntry(string kind, string code, int quantity) => new()
        {
            RoomId = roomId, Sequence = sequence, EventKey = eventKey, UserId = userId,
            CharacterId = characterId, Kind = kind, Code = code, Quantity = quantity
        };
    }

    public bool RollChance(decimal chancePercent) => chancePercent >= 100 ||
        chancePercent > 0 && (decimal)_random.NextDouble() * 100 < chancePercent;

    public IReadOnlyList<RewardDropPreview> GetDropPreview(string rewardCode, bool isClear)
    {
        var bundles = isClear ? _clears : _kills;
        if (!bundles.TryGetValue(rewardCode, out var bundle)) return [];
        return bundle.Drops.Select(drop => new RewardDropPreview(
            drop.Kind,
            drop.Code,
            drop.Kind switch
            {
                "Consumable" => _consumables.FindItem(drop.Code)?.Name ?? drop.Code,
                "Weapon" => _weapons.FindItem(drop.Code)?.Name ?? drop.Code,
                "Material" => _materials?.FindItem(drop.Code)?.Name ?? drop.Code,
                "SoulImprint" => _soulImprints?.Find(drop.Code)?.Name ?? drop.Code,
                _ => drop.Code
            },
            drop.Quantity,
            drop.ChancePercent,
            drop.Kind == "Weapon" ? BuildWeaponPreview(drop.Code) : null)).ToList();
    }

    private WeaponDropPreview? BuildWeaponPreview(string code)
    {
        var item = _weapons.FindItem(code);
        if (item is null) return null;
        return new WeaponDropPreview(item.Element, item.ItemLevel, item.Attack, item.MaxHp,
            item.Skills.Select(grant =>
            {
                var skill = _weapons.FindSkill(grant.Code)!;
                return new WeaponDropSkillPreview(skill.Name, grant.Level,
                    _weapons.DescribeSkill(skill, grant.Level), grant.UnlockQualityRank);
            }).ToList());
    }

    public bool HasRewardProfile(string rewardCode, bool isClear) =>
        (isClear ? _clears : _kills).ContainsKey(rewardCode);

    public string Describe(RewardEntry entry) => entry.Kind switch
    {
        "Gold" => "金币",
        "Experience" => "经验",
        "Consumable" => _consumables.FindItem(entry.Code)?.Name ?? entry.Code,
        "Material" => _materials?.FindItem(entry.Code)?.Name ?? entry.Code,
        "Weapon" => DescribeWeapon(entry),
        "SoulImprint" => _soulImprints?.Find(entry.Code)?.Name ?? entry.Code,
        _ => entry.Code
    };

    private string DescribeWeapon(RewardEntry entry)
    {
        var snapshot = DeserializeWeapon(entry);
        var name = _weapons.FindItem(entry.Code)?.Name;
        return snapshot is null ? name ?? entry.Code :
            (name is null ? snapshot : snapshot with { Name = name }).DisplayName;
    }

    public static WeaponRewardSnapshot? DeserializeWeapon(RewardEntry entry) =>
        string.IsNullOrWhiteSpace(entry.WeaponSnapshotJson)
            ? null
            : JsonSerializer.Deserialize<WeaponRewardSnapshot>(entry.WeaponSnapshotJson);

    public CharacterWeapon MaterializeWeapon(WeaponRewardSnapshot snapshot, int characterId) =>
        _weapons.MaterializeReward(snapshot, characterId);

    public CharacterSoulImprint MaterializeSoulImprint(string code, int characterId) =>
        _soulImprints?.Materialize(code, characterId)
        ?? throw new InvalidOperationException($"Unknown soul imprint reward: {code}");

    public WeaponRewardSnapshot? FirstHuntWeapon(string dungeonCode)
    {
        if (!_grantFirstHuntWeapon || !FirstHuntCodes.Contains(dungeonCode) ||
            !_kills.TryGetValue(dungeonCode, out var bundle)) return null;
        var drop = bundle.Drops.FirstOrDefault(drop => drop.Kind == "Weapon" && drop.ChancePercent > 0);
        return drop is null ? null : _weapons.CreateRewardSnapshot(drop.Code) with { Origin = WeaponOrigin.Tutorial };
    }

    private static readonly HashSet<string> FirstHuntCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "durotar-valley-boar", "dun-morogh-snow-hare", "northshire-wolves",
        "mulgore-plainstrider-chick", "eversong-golden-lynx", "tirisfal-dusk-bat"
    };
}

public sealed record WeaponRewardSnapshot(string Code, string Name, ElementType Element, int Attack, int MaxHp,
    int ItemLevel, int SellGold, int DismantleFragments, List<WeaponRewardSkillSnapshot> Skills, int TemplateRevision = 0,
    WeaponOrigin Origin = WeaponOrigin.Drop, int QualityRank = 0)
{
    [JsonIgnore]
    public int EffectiveQualityRank => Math.Clamp(Math.Max(QualityRank, Skills.Sum(skill => skill.QualityBonusLevel)),
        0, WeaponRules.MaxQualityBonusLevels);
    [JsonIgnore]
    public string QualityName => WeaponRules.QualityName(EffectiveQualityRank);
    [JsonIgnore]
    public string DisplayName => EffectiveQualityRank == 0 ? Name : $"{QualityName}·{Name}";

    public CharacterWeapon ToCharacterWeapon(int characterId) => new()
    {
        CharacterId = characterId, WeaponCode = Code, Name = Name, Element = Element, TemplateRevision = TemplateRevision, Origin = Origin,
        Attack = Attack, MaxHp = MaxHp, ItemLevel = Math.Max(1, ItemLevel), SellGold = Math.Max(0, SellGold),
        DismantleFragments = Math.Max(1, DismantleFragments), QualityRank = EffectiveQualityRank,
        Skills = Skills.Select((skill, index) => new CharacterWeaponSkill
        {
            SlotIndex = index + 1, SkillCode = skill.Code,
            Level = skill.Level, BaseLevel = skill.Level, SpentFragments = 0
        }).ToList()
    };
}

public sealed record WeaponRewardSkillSnapshot(string Code, int Level, int QualityBonusLevel = 0);
public sealed record RewardDropPreview(string Kind, string Code, string Name, int Quantity, decimal ChancePercent,
    WeaponDropPreview? Weapon);
public sealed record WeaponDropPreview(ElementType Element, int ItemLevel, int Attack, int MaxHp,
    IReadOnlyList<WeaponDropSkillPreview> Skills);
public sealed record WeaponDropSkillPreview(string Name, int Level, string Description, int UnlockQualityRank = 0);
