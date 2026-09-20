using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class ConsumableTestFactory
{
    public static ConsumableCatalog Create() => new(Options.Create(new ConsumableOptions
    {
        Items =
        [
            new ConsumableItemOptions
            {
                Code = "minor-healing-potion",
                Name = "小型治疗药水",
                HealAmount = 20,
                CooldownRounds = 3,
                CooldownGroup = "healing"
            }
        ],
        DungeonVictoryDrops = new Dictionary<string, List<ConsumableDropOptions>>
        {
            ["slime-field"] = [new ConsumableDropOptions { ItemCode = "minor-healing-potion", Quantity = 1 }]
        }
    }));
}
