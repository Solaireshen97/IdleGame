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
    [Theory]
    [InlineData("ragefire-chasm")]
    [InlineData("frostspring-cavern")]
    [InlineData("kobold-mine")]
    [InlineData("windfury-nest")]
    [InlineData("dawn-ruins")]
    [InlineData("spider-canyon")]
    public void ProductionEntryDungeonsReuseRegionalHuntsWithValidPreparationRewards(string code)
    {
        var path = TestRepository.File("Game.Server", "appsettings.json");
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
        var world = WorldCatalog.LoadDefault();
        var dungeon = world.Dungeons.Single(dungeon => dungeon.Code == code);
        var monsters = encounters.CreateMonsters(dungeon);
        var hunts = world.Dungeons.Where(hunt => hunt.IsVisible && hunt.RegionCode == dungeon.RegionCode &&
            hunt.DungeonKind == "Hunt").OrderBy(hunt => hunt.SortOrder).ToList();

        Assert.Equal(4, encounters.GetWaveCount(dungeon));
        Assert.Equal(4, encounters.GetMonsterCount(dungeon));
        Assert.Equal(new[] { 1, 2, 3, 4 }, monsters.Select(monster => monster.WaveNumber));
        Assert.All(monsters, monster => Assert.Equal(1, monster.Position));
        Assert.Equal(hunts.Select(hunt => hunt.MonsterName), monsters.Take(3).Select(monster => monster.Name));
        Assert.All(monsters.Take(3), monster => Assert.False(monster.IsBoss));
        Assert.True(monsters[^1].IsBoss);
        Assert.Equal(DungeonRewardEligibility.ActualParticipants,
            encounters.ResolveRewardEligibility(code, DungeonRewardEligibility.CurrentSlots));
        var expectedWeapons = hunts.Select(hunt => rewards.GetDropPreview(hunt.Code, false)
            .First(drop => drop.Kind == "Weapon").Code).ToArray();
        for (var index = 0; index < 3; index++)
        {
            Assert.True(monsters[index].MaxHp > hunts[index].MonsterMaxHp);
            Assert.True(monsters[index].Attack > hunts[index].MonsterAttack);
            var drop = Assert.Single(rewards.GetDropPreview(monsters[index].RewardProfileCode, false));
            Assert.Equal(("Weapon", expectedWeapons[index], 15m), (drop.Kind, drop.Code, drop.ChancePercent));
        }
        Assert.DoesNotContain(rewards.GetDropPreview(monsters[^1].RewardProfileCode, false),
            reward => reward.Kind == "Weapon");
        Assert.Equal(expectedWeapons, rewards.GetDropPreview(code, true).Where(drop => drop.Kind == "Weapon")
            .Select(drop => drop.Code));
        Assert.All(rewards.GetDropPreview(code, true).Where(drop => drop.Kind == "Weapon"),
            drop => Assert.Equal(65m, drop.ChancePercent));
        Assert.Contains(rewards.GetDropPreview(code, true),
            reward => reward.Code == "weapon-fragment-t1" && reward.Quantity == 12);
        Assert.Contains(rewards.GetDropPreview($"{code}-first-clear", true),
            reward => reward.Code == "weapon-fragment-t1" && reward.Quantity == 20);
        Assert.Equal(200, monsters.Sum(monster => rewardOptions.MonsterKills[monster.RewardProfileCode].Gold) +
            rewardOptions.DungeonClears[code].Gold);
        Assert.Equal(250, monsters.Sum(monster => rewardOptions.MonsterKills[monster.RewardProfileCode].Experience) +
            rewardOptions.DungeonClears[code].Experience);
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
