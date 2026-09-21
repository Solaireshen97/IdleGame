using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public class DungeonEncounterCatalogTests
{
    [Fact]
    public void CreateMonsters_FlattensConfiguredWavesInBattleOrder()
    {
        var catalog = new DungeonEncounterCatalog(Options.Create(new DungeonEncounterOptions
        {
            Dungeons = new Dictionary<string, List<DungeonWaveOptions>>(StringComparer.OrdinalIgnoreCase)
            {
                ["slime-field"] =
                [
                    new() { Monsters = [new() { Name = "Slime", Element = ElementType.Wind, MaxHp = 30, Attack = 5, Defense = 1 }] },
                    new() { Monsters =
                    [
                        new() { Name = "Slime", Element = ElementType.Wind, MaxHp = 40, Attack = 6, Defense = 2 },
                        new() { Name = "King Slime", Element = ElementType.Wind, MaxHp = 70, Attack = 10, Defense = 3 }
                    ] }
                ]
            }
        }));

        var monsters = catalog.CreateMonsters(new Dungeon { Code = "slime-field" });

        Assert.Equal(3, monsters.Count);
        Assert.Collection(monsters,
            monster => Assert.Equal((1, 1, 30), (monster.WaveNumber, monster.Position, monster.MaxHp)),
            monster => Assert.Equal((2, 1, 40), (monster.WaveNumber, monster.Position, monster.MaxHp)),
            monster => Assert.Equal((2, 2, 70), (monster.WaveNumber, monster.Position, monster.MaxHp)));
        Assert.Equal(2, catalog.GetWaveCount(new Dungeon { Code = "slime-field" }));
        Assert.Equal(3, catalog.GetMonsterCount(new Dungeon { Code = "slime-field" }));
    }
}
