using System.Text.Json;
using Game.Client.Services;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class ContentNamingTests
{
    private static readonly string Root = TestRepository.Root;

    [Fact]
    public void EveryRenamedMonsterAndWeaponStillUsesItsAssignedImage()
    {
        var config = Configuration();
        using var monsterManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "assets", "monster-art", "manifest.json")));
        var artByName = monsterManifest.RootElement.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!, item => item.GetProperty("file").GetString()!);
        var encounters = config.GetSection(DungeonEncounterOptions.SectionName).Get<DungeonEncounterOptions>()!;
        foreach (var monster in encounters.Dungeons.Values.SelectMany(waves => waves).SelectMany(wave => wave.Monsters))
        {
            var expected = $"/art/monsters/{artByName[monster.Name]}";
            Assert.Equal(expected, MonsterArt.ForName(monster.Name));
            Assert.True(File.Exists(Path.Combine(Root, "Game.Client", "wwwroot", expected.TrimStart('/'))));
        }
        var weapons = config.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!;
        foreach (var weapon in weapons.Items)
        {
            Assert.Equal(WeaponArt.ForCode(weapon.Code), WeaponArt.ForName(weapon.Name));
            Assert.True(File.Exists(Path.Combine(Root, "Game.Client", "wwwroot", "art", "weapons", $"{weapon.Code}.png")));
        }
    }

    [Fact]
    public async Task RenamingExistingWeaponPreservesStatsSkillsQualityAndInvestment()
    {
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var template = catalog.FindItem("goldtooth-pickaxe")!;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var weapon = new CharacterWeapon
        {
            CharacterId = 1, WeaponCode = template.Code, TemplateRevision = template.Revision,
            Name = "金牙的精工矿镐", Element = ElementType.Earth, Attack = 99, MaxHp = 123,
            ItemLevel = 5, SellGold = 17, DismantleFragments = 8, Origin = WeaponOrigin.Drop,
            QualityRank = 3, IsLocked = true, EquippedSlotIndex = 1, Version = 7,
            Skills = [new CharacterWeaponSkill { SlotIndex = 1, SkillCode = "weapon-might",
                BaseLevel = 3, EnhancementLevel = 2, Level = 5, SpentFragments = 22 }]
        };
        db.Characters.Add(new Character { Id = 1, Name = "旧存档角色", Attack = 99, MaxHp = 123, Hp = 41 });
        db.CharacterWeapons.Add(weapon);
        await db.SaveChangesAsync();
        var skillId = weapon.Skills[0].Id;
        await DbInitializer.InitializeAsync(db, catalog);
        await DbInitializer.InitializeAsync(db, catalog);
        db.ChangeTracker.Clear();
        var saved = await db.CharacterWeapons.Include(item => item.Skills).SingleAsync();
        Assert.Equal(template.Name, saved.Name);
        Assert.Equal((template.Code, template.Revision, ElementType.Earth, 99, 123, 5, 17, 8),
            (saved.WeaponCode, saved.TemplateRevision, saved.Element, saved.Attack, saved.MaxHp,
                saved.ItemLevel, saved.SellGold, saved.DismantleFragments));
        Assert.Equal((WeaponOrigin.Drop, 3, true, (int?)1, 8),
            (saved.Origin, saved.QualityRank, saved.IsLocked, saved.EquippedSlotIndex, saved.Version));
        var skill = Assert.Single(saved.Skills);
        Assert.Equal((skillId, "weapon-might", 3, 2, 5, (int?)22),
            (skill.Id, skill.SkillCode, skill.BaseLevel, skill.EnhancementLevel, skill.Level, skill.SpentFragments));
        Assert.Equal(41, (await db.Characters.SingleAsync()).Hp);
    }

    [Fact]
    public void PendingRewardRenamePreservesItsRolledSnapshotAndQuality()
    {
        var config = Configuration();
        IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(config.GetSection(section).Get<T>()!);
        var weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
        var template = weapons.FindItem("goldtooth-pickaxe")!;
        var snapshot = new WeaponRewardSnapshot(template.Code, "金牙的精工矿镐", ElementType.Earth,
            77, 155, 5, 17, 8, [new WeaponRewardSkillSnapshot("weapon-might", 5)],
            template.Revision, WeaponOrigin.Drop, 2);
        var json = JsonSerializer.Serialize(snapshot);
        var entry = new RewardEntry { Kind = "Weapon", Code = template.Code, Quantity = 1, WeaponSnapshotJson = json };
        var rewards = new RewardCatalog(Bind<RewardOptions>(RewardOptions.SectionName),
            new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName)), weapons,
            new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName)),
            new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName)));
        Assert.Equal($"{snapshot.QualityName}·{template.Name}", rewards.Describe(entry));
        Assert.Equal(json, entry.WeaponSnapshotJson);
        var received = rewards.MaterializeWeapon(snapshot, 77);
        Assert.Equal(template.Name, received.Name);
        Assert.Equal((77, template.Code, template.Revision, 77, 155, 5, 17, 8, 2),
            (received.CharacterId, received.WeaponCode, received.TemplateRevision, received.Attack,
                received.MaxHp, received.ItemLevel, received.SellGold, received.DismantleFragments, received.QualityRank));
        Assert.Equal(("weapon-might", 5), (Assert.Single(received.Skills).SkillCode, received.Skills[0].Level));
        Assert.Equal(json, JsonSerializer.Serialize(snapshot));
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(Root, "Game.Server", "appsettings.json")).Build();
}
