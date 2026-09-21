using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class RewardTestFactory
{
    public static RewardService CreateService(GameDbContext db, ProgressionService progression,
        bool guaranteedWeapon = false) => new(db, CreateCatalog(guaranteedWeapon), progression);

    public static RewardCatalog CreateCatalog(bool guaranteedWeapon = false)
    {
        var weapons = new WeaponCatalog(Options.Create(new WeaponOptions
        {
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
