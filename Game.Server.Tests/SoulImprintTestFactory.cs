using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class SoulImprintTestFactory
{
    public static SoulImprintCatalog Create() => new(Options.Create(new SoulImprintOptions
    {
        Items =
        [
            new SoulImprintDefinitionOptions
            {
                Code = "deep-core", Name = "深岩监工的震核", Description = "造成伤害并施加破甲。",
                DungeonCode = "slime-field", Tier = 1, Element = ElementType.Earth,
                EffectType = SoulImprintEffectType.DamageArmorBreak, StatusCode = "armor-break", PowerPercent = 180,
                SecondaryPowerPercent = 20, DurationRounds = 3, InitialCooldownRounds = 3,
                CooldownRounds = 8, DismantleFragments = 25
            }
        ]
    }));
}
