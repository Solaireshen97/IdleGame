using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Game.Server.Tests;

public sealed class T1WeaponContentTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();

    [Fact]
    public void EveryHuntHasTargetLootAndAllTwentyFourFieldWeaponsAreReachable()
    {
        var configuration = Configuration();
        var rewards = configuration.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var world = WorldCatalog.LoadDefault();
        var ordinary = world.Dungeons.Where(dungeon => dungeon.IsVisible && dungeon.DungeonKind == "Hunt").ToList();
        Assert.Equal(42, ordinary.Count);
        var drops = ordinary.Select(dungeon => Assert.Single(rewards.MonsterKills[dungeon.Code].Drops, drop => drop.Kind == "Weapon")).ToList();
        var templates = drops.Select(drop => catalog.FindItem(drop.Code)!).DistinctBy(item => item.Code).ToList();
        Assert.Equal(24, templates.Count);
        Assert.All(templates.GroupBy(item => item.Element), group =>
        {
            Assert.Equal(4, group.Count());
            Assert.Equal(20, group.Average(item => item.Attack));
            Assert.Equal(50, group.Average(item => item.MaxHp));
        });
        Assert.All(templates, item =>
        {
            Assert.Equal(1, item.ItemLevel);
            Assert.Equal(new[] { 2, 1 }, item.Skills.Select(skill => skill.Level));
            Assert.Equal(40m, item.Attack + item.MaxHp / 2.5m);
        });
        Assert.All(drops.GroupBy(drop => drop.Code), group => Assert.InRange(group.Count(), 1, 3));
    }

    [Fact]
    public void EliteWeaponsHaveUniqueSourcesAndSixElementsHaveTwoSpecializationsEach()
    {
        var rewards = Configuration().GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var elites = WorldCatalog.LoadDefault().Dungeons.Where(dungeon => dungeon.DungeonKind == "Elite").ToList();
        var weapons = elites.Select(dungeon => catalog.FindItem(Assert.Single(
            rewards.MonsterKills[dungeon.Code].Drops, drop => drop.Kind == "Weapon").Code)!).ToList();
        Assert.Equal(12, weapons.Select(item => item.Code).Distinct().Count());
        Assert.All(weapons.GroupBy(item => item.Element), group => Assert.Equal(2, group.Count()));
        Assert.All(weapons, item =>
        {
            Assert.Equal(1, item.ItemLevel);
            Assert.Equal(new[] { 3, 3 }, item.Skills.Select(skill => skill.Level));
            Assert.Equal(46m, item.Attack + item.MaxHp / 2.5m);
        });
    }

    [Fact]
    public void ShopHasSixEqualEntryWeaponsAndBossRewardsRemainAboveFieldBudget()
    {
        var configuration = Configuration();
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var shop = configuration.GetSection(ShopOptions.SectionName).Get<ShopOptions>()!;
        var weapons = shop.Items.Where(item => item.Kind == "Weapon").ToList();
        Assert.Equal(6, weapons.Select(item => catalog.FindItem(item.Code)!.Element).Distinct().Count());
        Assert.All(weapons, product =>
        {
            var item = catalog.FindItem(product.Code)!;
            Assert.Equal(40, product.Price);
            Assert.Equal((18, 45, 1), (item.Attack, item.MaxHp, item.ItemLevel));
            Assert.Equal(1, Assert.Single(item.Skills).Level);
        });
        Assert.All(WorldCatalog.LoadDefault().Regions, region =>
        {
            var item = catalog.FindItem(region.FeaturedWeaponCode)!;
            Assert.InRange(item.Attack + item.MaxHp / 2.5m, 44, 46);
            Assert.Equal(new[] { 3, 2 }, item.Skills.Select(skill => skill.Level));
        });
    }

    [Fact]
    public void FinalTierHasOneLevelTenDungeonAndSixElementExchangeOptionsPlusDistinctBossDrop()
    {
        var configuration = Configuration();
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var dungeon = WorldCatalog.LoadDefault().Dungeons.Single(item => item.Code == "kobold-mine-depths");
        Assert.Equal((10, 10, "Dungeon"), (dungeon.MinimumLevel, dungeon.RecommendedLevel, dungeon.DungeonKind));
        var offers = configuration.GetSection(DungeonExchangeOptions.SectionName).Get<DungeonExchangeOptions>()!.Offers
            .Where(offer => offer.DungeonCode == dungeon.Code).ToList();
        Assert.Equal(6, offers.Select(offer => catalog.FindItem(offer.WeaponCode)!.Element).Distinct().Count());
        Assert.All(offers, offer => Assert.Equal((18, "deep-mine-token"), (offer.Cost, offer.CurrencyCode)));
        var rewards = configuration.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        var bossDrop = Assert.Single(rewards.MonsterKills["kobold-mine-depths-boss"].Drops, drop => drop.Kind == "Weapon");
        Assert.DoesNotContain(offers, offer => offer.WeaponCode == bossDrop.Code);
        var gear = offers.Select(offer => catalog.FindItem(offer.WeaponCode)!).Append(catalog.FindItem(bossDrop.Code)!);
        Assert.All(gear, item =>
        {
            Assert.Equal(1, item.ItemLevel);
            Assert.Equal(new[] { 4, 3 }, item.Skills.Select(skill => skill.Level));
            Assert.InRange(item.Attack + item.MaxHp / 2.5m, 48, 50);
        });
    }

    [Fact]
    public async Task ExistingWeaponTemplatesRebaseAtomicallyAndPreserveInvestmentAndSlots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var weapon = new CharacterWeapon { Id = 1, CharacterId = 1, WeaponCode = "goldtooth-pickaxe", Name = "旧金牙矿镐",
            Element = ElementType.Earth, Attack = 14, MaxHp = 38, ItemLevel = 9, IsLocked = true, EquippedSlotIndex = 1, QualityRank = 3,
            Skills = [new() { SlotIndex = 1, SkillCode = "weapon-attack", BaseLevel = 2, EnhancementLevel = 2, Level = 4 },
                new() { SlotIndex = 2, SkillCode = "weapon-health", BaseLevel = 1, EnhancementLevel = 1, Level = 2 }] };
        var retired = new CharacterWeapon { Id = 2, CharacterId = 1, WeaponCode = "grave-sickle", Name = "墓园镰刀",
            Element = ElementType.Dark, Attack = 5, MaxHp = 12, ItemLevel = 2, EquippedSlotIndex = 2,
            Skills = [new() { SlotIndex = 1, SkillCode = "weapon-critical", BaseLevel = 1, Level = 1 }] };
        db.AddRange(new User { Id = 1, UserName = "rebase", PasswordHash = "x", ActiveCharacterId = 1 },
            new Character { Id = 1, UserId = 1, Name = "测试", Attack = 19, MaxHp = 50, Hp = 25}, weapon, retired);
        await db.SaveChangesAsync();

        var catalog = T1WeaponEffectTests.ProductionCatalog();
        await DbInitializer.InitializeAsync(db, catalog);
        db.ChangeTracker.Clear();
        var rebased = await db.CharacterWeapons.Include(item => item.Skills).SingleAsync(item => item.Id == 1);
        var mapped = await db.CharacterWeapons.Include(item => item.Skills).SingleAsync(item => item.Id == 2);
        var character = await db.Characters.SingleAsync();
        Assert.Equal((22, 60, 1, 1), (rebased.Attack, rebased.MaxHp, rebased.ItemLevel, rebased.TemplateRevision));
        Assert.True(rebased.IsLocked);
        Assert.Equal(1, rebased.EquippedSlotIndex);
        Assert.Equal(3, rebased.QualityRank);
        Assert.Equal(("weapon-might", 3, 0, 2, 5), Skill(rebased, 1));
        Assert.Equal(("weapon-skill", 2, 0, 1, 3), Skill(rebased, 2));
        Assert.Equal("t1-wood-hilt-ritual-dagger", mapped.WeaponCode);
        Assert.Equal(ElementType.Dark, mapped.Element);
        Assert.Equal(2, mapped.EquippedSlotIndex);
        Assert.Equal((44, 105, 25), (character.Attack, character.MaxHp, character.Hp));
        Assert.Equal(112, TalentRules.EffectiveMaxHp(character));
        var version = rebased.Version;
        await DbInitializer.InitializeAsync(db, catalog);
        Assert.Equal(version, rebased.Version);
        Assert.Equal(4, await db.CharacterWeaponSkills.CountAsync());

        static (string, int, int, int, int) Skill(CharacterWeapon item, int slot)
        {
            var skill = item.Skills.Single(entry => entry.SlotIndex == slot);
            return (skill.SkillCode, skill.BaseLevel, skill.QualityBonusLevel, skill.EnhancementLevel, skill.Level);
        }
    }

    [Fact]
    public void PendingOldRewardSnapshotUsesNewTemplateWithoutRerollingItsQuality()
    {
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var pending = new WeaponRewardSnapshot("grave-sickle", "旧镰刀", ElementType.Dark, 4, 12, 2, 5, 1,
            [new WeaponRewardSkillSnapshot("weapon-critical", 1, 3)]);
        var actual = catalog.MaterializeReward(pending, 77);
        Assert.Equal("t1-wood-hilt-ritual-dagger", actual.WeaponCode);
        Assert.Equal(77, actual.CharacterId);
        Assert.Equal(ElementType.Dark, actual.Element);
        Assert.Equal(1, actual.TemplateRevision);
        Assert.Equal(3, actual.QualityRank);
        Assert.All(actual.Skills, skill => Assert.Equal(0, skill.QualityBonusLevel));
        Assert.Equal(2, actual.Skills[0].Level);
        Assert.Equal(2, actual.Skills.Count);
    }
}
