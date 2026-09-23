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
            },
            new ConsumableItemOptions
            {
                Code = "northshire-battle-draught",
                Name = "北郡战意药剂",
                Kind = "OperationPotion",
                AttackPercent = 15
            }
        ]
    }));
}
