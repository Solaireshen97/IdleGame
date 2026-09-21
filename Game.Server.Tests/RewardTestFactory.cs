using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class RewardTestFactory
{
    public static RewardService CreateService(GameDbContext db, ProgressionService progression,
        bool guaranteedWeapon = false, int qualityBonusLevels = 0) =>
        new(db, CreateCatalog(guaranteedWeapon, qualityBonusLevels), progression);

    public static RewardCatalog CreateCatalog(bool guaranteedWeapon = false, int qualityBonusLevels = 0)
    {
        if (qualityBonusLevels is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(qualityBonusLevels));
        var qualityWeights = new WeaponDropQualityWeightsOptions
        {
            Common = qualityBonusLevels == 0 ? 1 : 0,
            Uncommon = qualityBonusLevels == 1 ? 1 : 0,
            Rare = qualityBonusLevels == 2 ? 1 : 0,
            Epic = qualityBonusLevels == 3 ? 1 : 0
        };
        var weapons = new WeaponCatalog(Options.Create(new WeaponOptions
        {
            DropQualityWeights = qualityWeights,
            Items = [new WeaponTemplateOptions
            {
                Code = "gale-bow", Name = "疾风短弓", Element = ElementType.Wind,
                Attack = 7, MaxHp = 28,
                Skills = [new WeaponSkillGrantOptions { Code = "weapon-critical", Level = 2 }]
            }],
            Skills = [new WeaponSkillDefinitionOptions
            {
                Code = "weapon-critical", Name = "暴击率",
                EffectType = WeaponSkillEffectType.CriticalChancePercent, PercentPerLevel = 5
            }],
            StarterPacks = new Dictionary<string, List<string>> { ["knight"] = ["gale-bow"] }
        }));
        return new RewardCatalog(Options.Create(new RewardOptions
        {
            MonsterKills = new Dictionary<string, RewardBundleOptions>
            {
                ["slime-field"] = new RewardBundleOptions
                {
                    Gold = 3, Experience = 2,
                    Drops = guaranteedWeapon ? [new RewardDropOptions
                    {
                        Kind = "Weapon", Code = "gale-bow", Quantity = 1, ChancePercent = 100
                    }] : []
                }
            },
            DungeonClears = new Dictionary<string, RewardBundleOptions>
            {
                ["slime-field"] = new RewardBundleOptions
                {
                    Gold = 10, Experience = 8,
                    Drops = [new RewardDropOptions
                    {
                        Kind = "Consumable", Code = "minor-healing-potion", Quantity = 1, ChancePercent = 100
                    }]
                }
            }
        }), ConsumableTestFactory.Create(), weapons);
    }
}
