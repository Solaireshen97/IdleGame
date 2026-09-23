using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class ProfessionTestFactory
{
    public static ProfessionCatalog Create() => new(Options.Create(new ProfessionProgressionOptions
    {
        ExperienceToNextLevel = [2, 10, 10, 10, 10, 10, 10, 10, 10],
        GatheringExperiencePerCycle = 1,
        RareGatheringExperiencePerCycle = 10,
        AlchemyExperiencePerCycle = 1,
        TalentNodes =
        [
            new ProfessionTalentNodeOptions
            {
                Code = "gather-yield", ProfessionCode = ProfessionCatalog.GatheringCode,
                Name = "丰收", Description = "测试额外收获", EffectType = "ExtraYieldChancePercent",
                ValuePerRank = 100, MaxRank = 1, MinimumLevel = 2, Tier = 1
            },
            new ProfessionTalentNodeOptions
            {
                Code = "gather-pace", ProfessionCode = ProfessionCatalog.GatheringCode,
                Name = "快手", Description = "测试加速", EffectType = "CycleReductionSeconds",
                ValuePerRank = 1, MaxRank = 1, MinimumLevel = 3, Tier = 2,
                PrerequisiteCode = "gather-yield", PrerequisiteRank = 1
            },
            new ProfessionTalentNodeOptions
            {
                Code = "gather-rare", ProfessionCode = ProfessionCatalog.GatheringCode,
                Name = "珍草", Description = "测试稀有附加产物", EffectType = "RareBonusChancePercent",
                ValuePerRank = 100, MaxRank = 1, MinimumLevel = 2, Tier = 1
            },
            new ProfessionTalentNodeOptions
            {
                Code = "alchemy-save", ProfessionCode = ProfessionCatalog.AlchemyCode,
                Name = "节约", Description = "测试返还原料", EffectType = "IngredientSaveChancePercent",
                ValuePerRank = 100, MaxRank = 1, MinimumLevel = 2, Tier = 1
            },
            new ProfessionTalentNodeOptions
            {
                Code = "alchemy-yield", ProfessionCode = ProfessionCatalog.AlchemyCode,
                Name = "丰瓶", Description = "测试额外产出", EffectType = "ExtraYieldChancePercent",
                ValuePerRank = 100, MaxRank = 1, MinimumLevel = 2, Tier = 1
            }
        ]
    }));
}
