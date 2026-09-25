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

    [Theory]
    [InlineData("mage", "apprentice-wand", ElementType.Water)]
    [InlineData("hunter", "scout-longbow", ElementType.Wind)]
    [InlineData("rogue", "novice-dagger", ElementType.Dark)]
    public void NewBaseProfessionsReceiveDedicatedStarterWeapons(string professionCode, string weaponCode,
        ElementType element)
    {
        var weapon = Assert.Single(T1WeaponEffectTests.ProductionCatalog()
            .CreateStarterWeapons(7, professionCode));
        Assert.Equal(weaponCode, weapon.WeaponCode);
        Assert.Equal(element, weapon.Element);
        Assert.Equal(WeaponRules.MainSlotIndex, weapon.EquippedSlotIndex);
        Assert.Equal(WeaponOrigin.Starter, weapon.Origin);
    }

    [Fact]
    public void FinalTierHasSixLevelTenDungeonsAndEachHasSixElementWeaponsWithoutExtraBossWeapon()
    {
        var configuration = Configuration();
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var dungeons = WorldCatalog.LoadDefault().Dungeons.Where(item => item.DungeonKind == "Dungeon" &&
            item.MinimumLevel == 10 && item.RecommendedLevel == 10).ToList();
        Assert.Equal(6, dungeons.Count);
        var allOffers = configuration.GetSection(DungeonExchangeOptions.SectionName).Get<DungeonExchangeOptions>()!.Offers;
        var rewards = configuration.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        var soulImprints = configuration.GetSection(SoulImprintOptions.SectionName).Get<SoulImprintOptions>()!.Items;
        var encounters = configuration.GetSection(DungeonEncounterOptions.SectionName).Get<DungeonEncounterOptions>()!;
        var monsterCombat = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        var softEnrage = Assert.Single(monsterCombat.Skills, skill => skill.Code == "endgame-soft-enrage");
        Assert.Equal((80, 22, 80),
            (softEnrage.DamagePowerPercent, softEnrage.RoomRoundAtLeast, softEnrage.ForcedPriority));
        var hardEnrage = Assert.Single(monsterCombat.Skills, skill => skill.Code == "endgame-hard-enrage");
        Assert.Equal((900, 42, 100),
            (hardEnrage.DamagePowerPercent, hardEnrage.RoomRoundAtLeast, hardEnrage.ForcedPriority));
        var expectedEncounters = new Dictionary<string, (int[] Hp, int[] Attack, int[] Defense,
            int SkillChance, string SignatureSkill, int SignaturePower, int SignatureCooldown,
            string SignatureStatus, decimal StatusValue, int StatusMaxStacks, string StatusStacking,
            string PhaseSkill, int PhaseHp, string PhaseStatus)>
        {
            ["kobold-mine-depths"] = ([5200, 5200, 7000, 7000, 57000], [32, 32, 43, 43, 170],
                [15, 14, 16, 18, 24], 72, "goldtooth-smash", 165, 2,
                "crushed-armor", -12, 2, "AddStack", "goldtooth-roar", 55, "monster-attack-up"),
            ["plague-crypt-depths"] = ([4600, 4600, 6200, 6200, 52000], [32, 32, 40, 40, 165],
                [10, 10, 12, 12, 15], 85, "widow-miasma", 55, 3,
                "plague-venom", 7, 3, "AddStack", "widow-cocoon", 65, "silk-shell"),
            ["ragefire-heart"] = ([4500, 4500, 6200, 6200, 48000], [38, 38, 50, 50, 178],
                [10, 10, 11, 11, 15], 82, "ragefire-eruption", 90, 3,
                "searing-wound", 5, 2, "AddStack", "ragefire-fury", 65, "monster-attack-up"),
            ["frostspring-throne"] = ([5000, 5000, 6800, 6800, 56000], [32, 32, 42, 42, 170],
                [14, 14, 16, 16, 22], 78, "frostking-blizzard", 65, 3,
                "deep-chill", -10, 2, "AddStack", "frostking-armor", 70, "frost-armor"),
            ["windfury-spire"] = ([4300, 4300, 6000, 6000, 50000], [40, 40, 52, 52, 185],
                [10, 10, 11, 11, 14], 90, "matriarch-tempest", 75, 2,
                "static-charge", -5, 3, "AddStack", "matriarch-song", 60, "monster-attack-up"),
            ["dawn-core"] = ([4900, 4900, 6600, 6600, 55000], [35, 35, 46, 46, 178],
                [14, 14, 15, 15, 20], 80, "dawnwarden-nova", 70, 3,
                "arcane-weakness", -20, 1, "RefreshDuration", "dawnwarden-shield", 70, "dawn-barrier")
        };
        var expectedSoulImprints = new Dictionary<string, (ElementType Element, SoulImprintEffectType Effect,
            int Power, int Secondary, int Duration, int InitialCooldown, int Cooldown, int AutoHpThreshold,
            string? StatusCode)>
        {
            ["kobold-mine-depths"] = (ElementType.Earth, SoulImprintEffectType.DamageArmorBreak,
                150, 20, 3, 3, 8, 100, "armor-break"),
            ["plague-crypt-depths"] = (ElementType.Dark, SoulImprintEffectType.DamageEcho,
                170, 35, 0, 3, 7, 100, null),
            ["ragefire-heart"] = (ElementType.Fire, SoulImprintEffectType.Interrupt,
                125, 0, 0, 2, 6, 100, null),
            ["frostspring-throne"] = (ElementType.Water, SoulImprintEffectType.GuardCounter,
                115, 50, 0, 2, 7, 100, "soul-frost-guard"),
            ["windfury-spire"] = (ElementType.Wind, SoulImprintEffectType.CooldownReduction,
                0, 2, 0, 3, 7, 100, null),
            ["dawn-core"] = (ElementType.Light, SoulImprintEffectType.HealCleanse,
                24, 1, 0, 3, 8, 75, null)
        };
        var bossStatLines = new HashSet<(int Hp, int Attack, int Defense)>();
        foreach (var dungeon in dungeons)
        {
            var expected = expectedEncounters[dungeon.Code];
            var waves = encounters.Dungeons[dungeon.Code];
            Assert.Equal(3, waves.Count);
            var monsters = waves.SelectMany(wave => wave.Monsters).ToList();
            Assert.Equal(5, monsters.Count);
            Assert.Equal(expected.Hp, monsters.Select(monster => monster.MaxHp));
            Assert.Equal(expected.Attack, monsters.Select(monster => monster.Attack));
            Assert.Equal(expected.Defense, monsters.Select(monster => monster.Defense));
            Assert.All(monsters.Take(4), monster => Assert.False(monster.IsBoss));
            Assert.True(monsters[^1].IsBoss);
            Assert.Equal((monsters[^1].MaxHp, monsters[^1].Attack, monsters[^1].Defense),
                (dungeon.MonsterMaxHp, dungeon.MonsterAttack, dungeon.MonsterDefense));
            Assert.True(bossStatLines.Add((monsters[^1].MaxHp, monsters[^1].Attack, monsters[^1].Defense)));
            var bossProfile = monsterCombat.Profiles[monsters[^1].CombatProfileCode];
            Assert.Equal(expected.SkillChance, bossProfile.SkillUseChancePercent);
            Assert.Equal(5, bossProfile.Skills.Count);
            Assert.Contains(bossProfile.Skills, skill => skill.Code == softEnrage.Code);
            Assert.Contains(bossProfile.Skills, skill => skill.Code == hardEnrage.Code);
            var bossSkills = bossProfile.Skills.Select(profileSkill => monsterCombat.Skills
                .Single(skill => skill.Code == profileSkill.Code)).ToList();
            var signatureSkill = Assert.Single(bossSkills, skill => skill.Code == expected.SignatureSkill);
            Assert.Equal((expected.SignaturePower, expected.SignatureCooldown),
                (signatureSkill.DamagePowerPercent, signatureSkill.CooldownRounds));
            Assert.Contains(signatureSkill.Statuses, status => status.StatusCode == expected.SignatureStatus);
            var signatureStatus = Assert.Single(monsterCombat.StatusEffects,
                status => status.Code == expected.SignatureStatus);
            Assert.Equal((expected.StatusValue, expected.StatusMaxStacks, expected.StatusStacking),
                (signatureStatus.ValuePerStack, signatureStatus.MaxStacks, signatureStatus.Stacking));
            var forcedPhaseSkill = Assert.Single(bossSkills, skill => skill.ForcedPriority == 30);
            Assert.Equal((expected.PhaseSkill, expected.PhaseHp),
                (forcedPhaseSkill.Code, forcedPhaseSkill.SelfHpBelowPercent));
            Assert.Contains(forcedPhaseSkill.Statuses, status => status.StatusCode == expected.PhaseStatus);

            var dungeonOffers = allOffers.Where(offer => offer.DungeonCode == dungeon.Code).ToList();
            var offers = dungeonOffers.Where(offer => offer.RewardKind == "Weapon").ToList();
            Assert.Equal(6, offers.Count);
            Assert.Equal(6, offers.Select(offer => catalog.FindItem(offer.EffectiveRewardCode)!.Element).Distinct().Count());
            Assert.All(offers, offer => Assert.Equal(18, offer.Cost));
            var fragmentOffer = Assert.Single(dungeonOffers, offer => offer.RewardKind == "Material");
            Assert.Equal((1, "weapon-fragment-t1", 5),
                (fragmentOffer.Cost, fragmentOffer.EffectiveRewardCode, fragmentOffer.RewardQuantity));
            var soulDefinition = Assert.Single(soulImprints, imprint => imprint.DungeonCode == dungeon.Code);
            var expectedSoul = expectedSoulImprints[dungeon.Code];
            Assert.Equal((expectedSoul.Element, expectedSoul.Effect, expectedSoul.Power, expectedSoul.Secondary,
                    expectedSoul.Duration, expectedSoul.InitialCooldown, expectedSoul.Cooldown,
                    expectedSoul.AutoHpThreshold, expectedSoul.StatusCode),
                (soulDefinition.Element, soulDefinition.EffectType, soulDefinition.PowerPercent,
                    soulDefinition.SecondaryPowerPercent, soulDefinition.DurationRounds,
                    soulDefinition.InitialCooldownRounds, soulDefinition.CooldownRounds,
                    soulDefinition.AutoHpThresholdPercent, soulDefinition.StatusCode));
            if (soulDefinition.StatusCode is not null)
            {
                var soulStatus = Assert.Single(monsterCombat.StatusEffects,
                    status => status.Code == soulDefinition.StatusCode);
                Assert.Equal((decimal)soulDefinition.SecondaryPowerPercent,
                    decimal.Abs(soulStatus.ValuePerStack));
            }
            var soulOffer = Assert.Single(dungeonOffers, offer => offer.RewardKind == "SoulImprint");
            Assert.Equal((100, soulDefinition.Code), (soulOffer.Cost, soulOffer.EffectiveRewardCode));
            var soulDrop = Assert.Single(rewards.DungeonClears[dungeon.Code].Drops,
                drop => drop.Kind == "SoulImprint");
            Assert.Equal((soulDefinition.Code, 1, 1m),
                (soulDrop.Code, soulDrop.Quantity, soulDrop.ChancePercent));
            var weaponDrops = rewards.DungeonClears[dungeon.Code].Drops
                .Where(drop => drop.Kind == "Weapon").ToList();
            Assert.Equal(6, weaponDrops.Count);
            Assert.Equal(offers.Select(offer => offer.EffectiveRewardCode).Order(),
                weaponDrops.Select(drop => drop.Code).Order());
            Assert.All(weaponDrops, drop => Assert.Equal((1, 3m), (drop.Quantity, drop.ChancePercent)));
            Assert.DoesNotContain(rewards.MonsterKills[$"{dungeon.Code}-boss"].Drops, drop => drop.Kind == "Weapon");
            Assert.All(offers.Select(offer => catalog.FindItem(offer.EffectiveRewardCode)!), item =>
            {
                Assert.Equal(1, item.ItemLevel);
                Assert.Equal(new[] { 4, 3 }, item.Skills.Select(skill => skill.Level));
                Assert.InRange(item.Attack + item.MaxHp / 2.5m, 47, 51);
            });
        }
        Assert.Equal(6, bossStatLines.Count);
        Assert.Equal(6, soulImprints.Select(imprint => imprint.Element).Distinct().Count());
        Assert.Equal(6, soulImprints.Select(imprint => imprint.EffectType).Distinct().Count());
        Assert.Equal(36, dungeons.SelectMany(dungeon => allOffers.Where(offer =>
            offer.DungeonCode == dungeon.Code && offer.RewardKind == "Weapon"))
            .Select(offer => offer.EffectiveRewardCode).Distinct().Count());
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
