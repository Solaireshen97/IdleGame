using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class SkillTestFactory
{
    public static SkillCatalog Create() => new(Options.Create(new SkillOptions
    {
        Professions =
        [
            new ProfessionOptions { Code = "knight", Name = "骑士", StartingSkills = ["knight-strike", "knight-guard"] },
            new ProfessionOptions { Code = "cleric", Name = "牧师", StartingSkills = ["cleric-heal", "cleric-smite"] }
        ],
        Abilities =
        [
            new CombatSkillOptions { Code = "knight-strike", ProfessionCode = "knight", Name = "盾击", Description = "额外伤害", EffectType = "Damage", Power = 8, CooldownRounds = 2 },
            new CombatSkillOptions { Code = "knight-guard", ProfessionCode = "knight", Name = "守护", Description = "减伤", EffectType = "Guard", Power = 50, CooldownRounds = 3 },
            new CombatSkillOptions { Code = "cleric-heal", ProfessionCode = "cleric", Name = "治疗术", Description = "治疗队友", EffectType = "Heal", Power = 20, CooldownRounds = 3 },
            new CombatSkillOptions { Code = "cleric-smite", ProfessionCode = "cleric", Name = "圣光击", Description = "额外伤害", EffectType = "Damage", Power = 6, CooldownRounds = 2 },
            new CombatSkillOptions { Code = "knight-break", ProfessionCode = "knight", Name = "破甲斩", Description = "额外伤害", EffectType = "Damage", Power = 12, CooldownRounds = 3 },
            new CombatSkillOptions { Code = "knight-wall", ProfessionCode = "knight", Name = "铁壁", Description = "减伤", EffectType = "Guard", Power = 65, CooldownRounds = 4 },
            new CombatSkillOptions { Code = "knight-assault", ProfessionCode = "knight", Name = "猛攻", Description = "额外伤害", EffectType = "Damage", Power = 18, CooldownRounds = 4 },
            new CombatSkillOptions { Code = "knight-verdict", ProfessionCode = "knight", Name = "誓约裁决", Description = "额外伤害", EffectType = "Damage", Power = 30, CooldownRounds = 5 },
            new CombatSkillOptions { Code = "cleric-blessing", ProfessionCode = "cleric", Name = "祈福", Description = "治疗", EffectType = "Heal", Power = 30, CooldownRounds = 4 },
            new CombatSkillOptions { Code = "cleric-sanctuary", ProfessionCode = "cleric", Name = "庇护", Description = "减伤", EffectType = "Guard", Power = 45, CooldownRounds = 4 },
            new CombatSkillOptions { Code = "cleric-mercy", ProfessionCode = "cleric", Name = "慈悲之光", Description = "治疗", EffectType = "Heal", Power = 40, CooldownRounds = 5 },
            new CombatSkillOptions { Code = "cleric-judgment", ProfessionCode = "cleric", Name = "神圣审判", Description = "额外伤害", EffectType = "Damage", Power = 24, CooldownRounds = 5 }
        ],
        TalentNodes =
        [
            new SkillTalentNodeOptions { Code = "knight-vanguard", ProfessionCode = "knight", Name = "先锋", Description = "学习破甲斩", SkillCode = "knight-break", Cost = 1, Tier = 1, Column = 1, RequiredTalentType = TalentType.Attack, RequiredTalentRank = 1 },
            new SkillTalentNodeOptions { Code = "knight-fortitude", ProfessionCode = "knight", Name = "坚守", Description = "学习铁壁", SkillCode = "knight-wall", Cost = 1, Tier = 1, Column = 2, RequiredTalentType = TalentType.Defense, RequiredTalentRank = 1 },
            new SkillTalentNodeOptions { Code = "knight-offense", ProfessionCode = "knight", Name = "锐意", Description = "学习猛攻", SkillCode = "knight-assault", Cost = 1, Tier = 2, Column = 1, RequiredTalentType = TalentType.Attack, RequiredTalentRank = 2, Prerequisites = ["knight-vanguard"] },
            new SkillTalentNodeOptions { Code = "knight-oath", ProfessionCode = "knight", Name = "骑士誓约", Description = "学习誓约裁决", SkillCode = "knight-verdict", Cost = 1, Tier = 3, Column = 3, RequiredTalentType = TalentType.Health, RequiredTalentRank = 1, Prerequisites = ["knight-fortitude", "knight-offense"] },
            new SkillTalentNodeOptions { Code = "cleric-light", ProfessionCode = "cleric", Name = "圣光启迪", Description = "学习祈福", SkillCode = "cleric-blessing", Cost = 1, Tier = 1, Column = 3, RequiredTalentType = TalentType.Health, RequiredTalentRank = 1 },
            new SkillTalentNodeOptions { Code = "cleric-ward", ProfessionCode = "cleric", Name = "守护祷言", Description = "学习庇护", SkillCode = "cleric-sanctuary", Cost = 1, Tier = 1, Column = 2, RequiredTalentType = TalentType.Defense, RequiredTalentRank = 1 },
            new SkillTalentNodeOptions { Code = "cleric-compassion", ProfessionCode = "cleric", Name = "慈悲", Description = "学习慈悲之光", SkillCode = "cleric-mercy", Cost = 1, Tier = 2, Column = 3, RequiredTalentType = TalentType.Health, RequiredTalentRank = 2, Prerequisites = ["cleric-light"] },
            new SkillTalentNodeOptions { Code = "cleric-devotion", ProfessionCode = "cleric", Name = "神圣信念", Description = "学习神圣审判", SkillCode = "cleric-judgment", Cost = 1, Tier = 1, Column = 1, RequiredTalentType = TalentType.Attack, RequiredTalentRank = 1 }
        ]
    }));
}
