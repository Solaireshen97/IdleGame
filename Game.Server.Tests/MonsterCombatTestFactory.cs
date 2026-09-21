using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class MonsterCombatTestFactory
{
    public static MonsterCombatCatalog CreateCatalog() => new(Options.Create(new MonsterCombatOptions
    {
        StatusEffects =
        [
            new() { Code = "armor-break", Name = "破甲", Description = "受到伤害提高。", EffectType = "ReductionPercent", ValuePerStack = -20 },
            new() { Code = "poison", Name = "中毒", Description = "回合末受到伤害。", EffectType = "DamageOverTime", ValuePerStack = 4, MaxStacks = 3, Stacking = "AddStack" },
            new() { Code = "slime-shell", Name = "黏液硬化", Description = "受到伤害降低。", EffectType = "ReductionPercent", ValuePerStack = 20, IsPositive = true }
        ],
        Skills =
        [
            new()
            {
                Code = "acid", Name = "腐蚀喷射", Description = "攻击并施加破甲。", TargetType = "Front",
                DamagePowerPercent = 120, CooldownRounds = 2,
                Statuses = [new() { StatusCode = "armor-break", DurationRounds = 2 }]
            },
            new()
            {
                Code = "toxic", Name = "剧毒爆发", Description = "攻击全体并施加中毒。", TargetType = "AllAlive",
                DamagePowerPercent = 80, CooldownRounds = 3, IsInterruptible = false, DangerLevel = "Deadly",
                Statuses = [new() { StatusCode = "poison", DurationRounds = 2 }]
            },
            new()
            {
                Code = "harden", Name = "黏液硬化", Description = "获得减伤。", TargetType = "Self",
                CooldownRounds = 3, Statuses = [new() { StatusCode = "slime-shell", DurationRounds = 2 }]
            }
        ],
        Profiles = new Dictionary<string, MonsterCombatProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["acid-slime"] = new() { SkillUseChancePercent = 100, Skills = [new() { Code = "acid", Weight = 1 }] },
            ["toxic-slime"] = new() { SkillUseChancePercent = 100, Skills = [new() { Code = "toxic", Weight = 1 }] },
            ["hardened-slime"] = new() { SkillUseChancePercent = 100, Skills = [new() { Code = "harden", Weight = 1 }] }
        }
    }));
}
