using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public class DungeonEncounterCatalogTests
{
    [Fact]
    public void ProductionConfigurationDefinesKoboldMineAndValidRewardProfiles()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"));
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var consumableOptions = new ConsumableOptions();
        var weaponOptions = new WeaponOptions();
        var rewardOptions = new RewardOptions();
        var combatOptions = new MonsterCombatOptions();
        var encounterOptions = new DungeonEncounterOptions();
        configuration.GetSection(ConsumableOptions.SectionName).Bind(consumableOptions);
        configuration.GetSection(WeaponOptions.SectionName).Bind(weaponOptions);
        configuration.GetSection(RewardOptions.SectionName).Bind(rewardOptions);
        configuration.GetSection(MonsterCombatOptions.SectionName).Bind(combatOptions);
        configuration.GetSection(DungeonEncounterOptions.SectionName).Bind(encounterOptions);

        var consumables = new ConsumableCatalog(Options.Create(consumableOptions));
        var weapons = new WeaponCatalog(Options.Create(weaponOptions));
        var materialOptions = new MaterialOptions();
        configuration.GetSection(MaterialOptions.SectionName).Bind(materialOptions);
        var materials = new MaterialCatalog(Options.Create(materialOptions));
        var soulOptions = new SoulImprintOptions();
        configuration.GetSection(SoulImprintOptions.SectionName).Bind(soulOptions);
        var soulImprints = new SoulImprintCatalog(Options.Create(soulOptions));
        var rewards = new RewardCatalog(Options.Create(rewardOptions), consumables, weapons, materials, soulImprints);
        var combat = new MonsterCombatCatalog(Options.Create(combatOptions));
        var encounters = new DungeonEncounterCatalog(Options.Create(encounterOptions), combat, rewards);
        var mine = new Dungeon { Code = "kobold-mine" };
        var monsters = encounters.CreateMonsters(mine);

        Assert.Equal(4, encounters.GetWaveCount(mine));
        Assert.Equal(7, encounters.GetMonsterCount(mine));
        Assert.Equal("金牙", monsters[^1].Name);
        Assert.True(monsters[^1].IsBoss);
        Assert.Equal("kobold-mine-goldtooth", monsters[^1].RewardProfileCode);
        Assert.Contains(rewards.GetDropPreview("kobold-mine-goldtooth", false),
            reward => reward.Name == "金牙的精工矿镐" && reward.ChancePercent == 18);
        Assert.Contains(rewards.GetDropPreview("kobold-mine", true),
            reward => reward.Name == "矿洞徽记" && reward.Quantity == 1);
        Assert.Contains(rewards.GetDropPreview("kobold-mine-first-clear", true),
            reward => reward.Name == "矿洞徽记" && reward.Quantity == 2);
    }

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
