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
        TestRepository.File("Game.Server", "appsettings.json"))).Build();

    [Fact]
    public void EveryConfiguredWeaponSkillStartsAtLevelOneAndUsesQualityCapacity()
    {
        var options = Configuration().GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!;
        Assert.NotEmpty(options.Items);
        Assert.All(options.Items.SelectMany(item => item.Skills), skill => Assert.Equal(1, skill.Level));
        Assert.Equal(new[] { 4, 6, 8, 10 }, Enumerable.Range(0, 4).Select(WeaponRules.MaximumSkillLevel));
        Assert.Equal(new[] { 3, 5, 7, 9 }, Enumerable.Range(0, 4)
            .Select(rank => WeaponRules.EnhancementLimit(rank, 1)));
        Assert.Equal(10, WeaponRules.MaxSkillLevel);
    }

    [Fact]
    public void EveryHuntHasTargetLootAndFirstHuntsOnlyDropTheirMainWeapon()
    {
        var configuration = Configuration();
        var rewards = configuration.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var world = WorldCatalog.LoadDefault();
        var ordinary = world.Dungeons.Where(dungeon => dungeon.IsVisible && dungeon.DungeonKind == "Hunt").ToList();
        Assert.Equal(18, ordinary.Count);
        var drops = ordinary.SelectMany(dungeon => rewards.MonsterKills[dungeon.Code].Drops
            .Where(drop => drop.Kind == "Weapon")).ToList();
        var templates = drops.Select(drop => catalog.FindItem(drop.Code)!).DistinctBy(item => item.Code).ToList();
        Assert.Equal(18, templates.Count);
        Assert.All(templates.GroupBy(item => item.Element), group => Assert.Equal(3, group.Count()));
        Assert.All(templates, item =>
        {
            Assert.Equal(1, item.ItemLevel);
            Assert.All(item.Skills, skill => Assert.Equal(1, skill.Level));
        });
        var redesigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "t1-candle-staff", "t1-ice-tusk-mallet", "t1-stone-edge-hatchet",
            "t1-feather-short-staff", "t1-sentry-old-sword", "t1-dim-apprentice-staff",
            "t1-fishbone-knife", "t1-frostmane-hunting-spear",
            "t1-burning-blade-hatchet", "t1-soot-iron-hammer",
            "t1-boar-tusk-club", "t1-chipped-mining-pick",
            "t1-hide-wrapped-club", "t1-sinew-shortbow",
            "t1-dusty-prayer-mace", "t1-copper-ring-ritual-staff",
            "t1-grave-thorn-staff", "t1-wood-hilt-ritual-dagger"
        };
        Assert.All(templates.Where(item => !redesigned.Contains(item.Code)), item =>
            Assert.Equal(40m, item.Attack + item.MaxHp / 2.5m));
        Assert.All(ordinary, dungeon => Assert.InRange(
            rewards.MonsterKills[dungeon.Code].Drops.Count(drop => drop.Kind == "Weapon"),
            dungeon.RecommendedLevel == 1 ? 1 : 2, 3));
    }

    [Fact]
    public void EliteWeaponsHaveUniqueSourcesAndSixElementsHaveTwoSpecializationsEach()
    {
        var rewards = Configuration().GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var elites = WorldCatalog.LoadDefault().Dungeons.Where(dungeon => dungeon.DungeonKind == "Elite").ToList();
        var weapons = elites.Select(dungeon => catalog.FindItem(Assert.Single(
            rewards.MonsterKills[dungeon.Code].Drops,
            drop => drop.Kind == "Weapon" && drop.ChancePercent == 3).Code)!).ToList();
        Assert.Equal(12, weapons.Select(item => item.Code).Distinct().Count());
        Assert.All(weapons.GroupBy(item => item.Element), group => Assert.Equal(2, group.Count()));
        Assert.All(weapons, item =>
        {
            Assert.Equal(1, item.ItemLevel);
            Assert.All(item.Skills, skill => Assert.Equal(1, skill.Level));
            Assert.Equal(46m, item.Attack + item.MaxHp / 2.5m);
            Assert.Single(rewards.MonsterKills.Values, bundle => bundle.Drops.Any(drop => drop.Code == item.Code));
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
            Assert.Equal((90, 110, 1), (item.Attack, item.MaxHp, item.ItemLevel));
            Assert.Equal(("weapon-health-small", 1), (Assert.Single(item.Skills).Code, item.Skills[0].Level));
        });
        Assert.All(WorldCatalog.LoadDefault().Regions, region =>
        {
            var item = catalog.FindItem(region.FeaturedWeaponCode)!;
            Assert.InRange(item.Attack + item.MaxHp / 2.5m, 44, 46);
            Assert.All(item.Skills, skill => Assert.Equal(1, skill.Level));
        });
    }

    [Theory]
    [InlineData("swordsman", "t1-shop-fire", ElementType.Fire)]
    [InlineData("acolyte", "t1-shop-light", ElementType.Light)]
    [InlineData("mage", "t1-shop-water", ElementType.Water)]
    [InlineData("hunter", "t1-shop-wind", ElementType.Wind)]
    [InlineData("rogue", "t1-shop-dark", ElementType.Dark)]
    public void BaseProfessionsReceiveMatchingShopWeaponsAsStarters(string professionCode, string weaponCode,
        ElementType element)
    {
        var weapons = T1WeaponEffectTests.ProductionCatalog().CreateStarterWeapons(7, professionCode);
        Assert.Equal(WeaponRules.SlotCount, weapons.Count);
        Assert.Equal(Enumerable.Range(1, WeaponRules.SlotCount), weapons.Select(item => item.EquippedSlotIndex!.Value));
        Assert.True(weapons[0].IsLocked);
        Assert.All(weapons.Skip(1), item => Assert.False(item.IsLocked));
        var weapon = weapons[0];
        Assert.All(weapons, item => Assert.Equal(weaponCode, item.WeaponCode));
        Assert.Equal(weaponCode, weapon.WeaponCode);
        Assert.Equal(element, weapon.Element);
        Assert.Equal(WeaponRules.MainSlotIndex, weapon.EquippedSlotIndex);
        Assert.Equal(WeaponOrigin.Starter, weapon.Origin);
        Assert.Equal((90, 110, 8), (weapon.Attack, weapon.MaxHp, weapon.SellGold));
        Assert.Equal(0, weapon.QualityRank);
        Assert.All(weapons, item => Assert.Equal(("weapon-health-small", 1),
            (Assert.Single(item.Skills).SkillCode, item.Skills[0].Level)));
        var bonuses = T1WeaponEffectTests.ProductionCatalog().CalculateBonuses(weapons);
        Assert.Equal(20m, bonuses.HealthPercent);
        var product = Assert.Single(Configuration().GetSection(ShopOptions.SectionName).Get<ShopOptions>()!.Items,
            item => item.Code == weaponCode && item.Kind == "Weapon");
        Assert.Equal(40, product.Price);
    }

    [Fact]
    public void FinalTierHasSixLevelTenDungeonsWithTwoMatchingDeepWeaponsAndSupplementaryExchanges()
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
        var expectedSoulImprints = new Dictionary<string, (ElementType Element, SoulImprintEffectType Effect,
            int Power, int Secondary, int Duration, int InitialCooldown, int Cooldown, string AutoCondition,
            string? StatusCode)>
        {
            ["kobold-mine-depths"] = (ElementType.Earth, SoulImprintEffectType.DamageArmorBreak,
                70, 5, 1, 3, 8, "Always", "soul-earth-vulnerability"),
            ["plague-crypt-depths"] = (ElementType.Dark, SoulImprintEffectType.DamageEcho,
                110, 35, 0, 3, 7, "Always", null),
            ["ragefire-heart"] = (ElementType.Fire, SoulImprintEffectType.Interrupt,
                100, 0, 0, 2, 7, "InterruptibleIntent", null),
            ["frostspring-throne"] = (ElementType.Water, SoulImprintEffectType.GuardCounter,
                90, 30, 0, 2, 7, "SelfHpBelowThreshold", "soul-frost-guard"),
            ["windfury-spire"] = (ElementType.Wind, SoulImprintEffectType.CooldownReduction,
                60, 1, 0, 3, 7, "Always", null),
            ["dawn-core"] = (ElementType.Light, SoulImprintEffectType.HealCleanse,
                10, 1, 0, 3, 9, "AllyHpBelowThreshold", null)
        };
        // Independently calibrated bosses may share a panel; their identities remain distinct.
        var bossNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dungeon in dungeons)
        {
            var waves = encounters.Dungeons[dungeon.Code];
            var isFireDeep = dungeon.Code == "ragefire-heart";
            Assert.Equal(5, waves.Count);
            var monsters = waves.SelectMany(wave => wave.Monsters).ToList();
            Assert.Equal(5, monsters.Count);
            Assert.All(monsters.Take(4), monster => Assert.False(monster.IsBoss));
            Assert.True(monsters[^1].IsBoss);
            Assert.Equal((monsters[^1].MaxHp, monsters[^1].Attack, monsters[^1].Defense),
                (dungeon.MonsterMaxHp, dungeon.MonsterAttack, dungeon.MonsterDefense));
            Assert.True(bossNames.Add(monsters[^1].Name));
            var bossProfile = monsterCombat.Profiles[monsters[^1].CombatProfileCode];
            if (isFireDeep)
            {
                Assert.Equal(new[] { 420000, 500000, 580000, 650000, 1100000 }, monsters.Select(monster => monster.MaxHp));
                Assert.Equal(new[] { 140, 170, 190, 210, 240 }, monsters.Select(monster => monster.Attack));
                Assert.All(monsters, monster => Assert.Equal(0, monster.Defense));
                Assert.All(waves, wave => Assert.Single(wave.Monsters));
                Assert.Equal("熔火督军", monsters[^1].Name);
                Assert.Equal("/art/monsters/monster-082.png", Game.Client.Services.MonsterArt.ForName(monsters[^1].Name));
                Assert.Equal("/art/monsters/monster-082.png", Game.Client.Services.MonsterArt.ForName("烬核督战者"));
                Assert.Equal(100, bossProfile.SkillUseChancePercent);
                Assert.Equal(new[] { "fire-deep-lv1-warlord-slash", "fire-deep-lv1-warlord-eruption" },
                    bossProfile.Skills.Select(skill => skill.Code));
                var bossSkills = bossProfile.Skills.Select(entry => monsterCombat.Skills.Single(skill => skill.Code == entry.Code)).ToList();
                Assert.Equal(new[] { 160, 100 }, bossSkills.Select(skill => skill.DamagePowerPercent));
                Assert.Equal(new[] { "Front", "AllAlive" }, bossSkills.Select(skill => skill.TargetType));
                Assert.All(bossSkills, skill =>
                {
                    Assert.Equal(3, skill.CooldownRounds);
                    Assert.Null(skill.SelfHpBelowPercent);
                    Assert.Null(skill.RoomRoundAtLeast);
                    Assert.Empty(skill.Statuses);
                });
                Assert.True(bossSkills[0].IsInterruptible);
                Assert.False(bossSkills[1].IsInterruptible);
            }
            else if (dungeon.Code == "frostspring-throne")
            {
                Assert.Equal(new[] { 420000, 490000, 560000, 650000, 1050000 }, monsters.Select(monster => monster.MaxHp));
                Assert.Equal(new[] { 150, 190, 210, 250, 290 }, monsters.Select(monster => monster.Attack));
                Assert.All(monsters, monster => Assert.Equal(0, monster.Defense));
                Assert.All(waves, wave => Assert.Single(wave.Monsters));
                Assert.Equal("凝霜冠主", monsters[^1].Name);
                Assert.True(bossProfile.UseEncounterLocalSkillClock);
                Assert.Equal(100, bossProfile.SkillUseChancePercent);
                Assert.Equal(new[] { "water-deep-lv1-king-strike", "water-deep-lv1-king-tide" },
                    bossProfile.Skills.Select(skill => skill.Code));
                var bossSkills = bossProfile.Skills.Select(entry => monsterCombat.Skills.Single(skill => skill.Code == entry.Code)).ToList();
                Assert.Equal(new[] { 160, 80 }, bossSkills.Select(skill => skill.DamagePowerPercent));
                Assert.Equal(new[] { "Front", "AllAlive" }, bossSkills.Select(skill => skill.TargetType));
                Assert.Equal(new[] { 1, 3 }, bossSkills.Select(skill => skill.InitialCooldownRounds));
                Assert.All(bossSkills, skill =>
                {
                    Assert.Equal(3, skill.CooldownRounds);
                    Assert.Null(skill.SelfHpBelowPercent);
                    Assert.Null(skill.RoomRoundAtLeast);
                    Assert.Equal("water-deep-lv1-chill-10", Assert.Single(skill.Statuses).StatusCode);
                    Assert.Equal(2, skill.Statuses[0].DurationRounds);
                });
                Assert.True(bossSkills[0].IsInterruptible);
                Assert.False(bossSkills[1].IsInterruptible);
            }
            else if (dungeon.Code == "kobold-mine-depths")
            {
                Assert.Equal(new[] { 380000, 480000, 550000, 620000, 1050000 }, monsters.Select(monster => monster.MaxHp));
                Assert.Equal(new[] { 150, 190, 210, 250, 290 }, monsters.Select(monster => monster.Attack));
                Assert.All(monsters, monster => Assert.Equal(0, monster.Defense));
                Assert.All(waves, wave => Assert.Single(wave.Monsters));
                Assert.Equal("沉岩督造者", monsters[^1].Name);
                Assert.True(bossProfile.UseEncounterLocalSkillClock);
                Assert.Equal(100, bossProfile.SkillUseChancePercent);
                Assert.Equal(new[] { "earth-deep-lv1-overseer-pick", "earth-deep-lv1-overseer-rockfall",
                    "earth-deep-lv1-overseer-armor" }, bossProfile.Skills.Select(skill => skill.Code));
                Assert.DoesNotContain(bossProfile.Skills, skill => skill.Code == softEnrage.Code || skill.Code == hardEnrage.Code);
                Assert.Null(bossProfile.FireCore);
                Assert.Null(bossProfile.DeepCold);
            }
            else if (dungeon.Code == "windfury-spire")
            {
                Assert.Equal(new[] { 430000, 520000, 570000, 670000, 1080000 }, monsters.Select(monster => monster.MaxHp));
                Assert.Equal(new[] { 150, 185, 215, 240, 280 }, monsters.Select(monster => monster.Attack));
                Assert.All(monsters, monster => Assert.Equal(0, monster.Defense));
                Assert.All(waves, wave => Assert.Single(wave.Monsters));
                Assert.Equal("岚穹巢主", monsters[^1].Name);
                Assert.True(bossProfile.UseEncounterLocalSkillClock);
                Assert.Equal(100, bossProfile.SkillUseChancePercent);
                Assert.Equal(new[] { "wind-deep-lv1-matriarch-dive", "wind-deep-lv1-matriarch-tempest",
                    "wind-deep-lv1-matriarch-song" }, bossProfile.Skills.Select(skill => skill.Code));
                Assert.DoesNotContain(bossProfile.Skills, skill => skill.Code == softEnrage.Code || skill.Code == hardEnrage.Code);
                var randomPierce = monsterCombat.Skills.Single(s => s.Code == "wind-deep-lv1-hunter-pierce");
                Assert.Equal("RandomAlive", randomPierce.TargetType);
                Assert.All(bossProfile.Skills.Select(e => monsterCombat.Skills.Single(s => s.Code == e.Code)),
                    s => Assert.DoesNotContain(s.Statuses, a => a.StatusCode == "static-charge"));
            }
            else if (dungeon.Code == "dawn-core")
            {
                Assert.Equal(new[] { 380000, 465000, 550000, 610000, 1020000 }, monsters.Select(monster => monster.MaxHp));
                Assert.Equal(new[] { 150, 185, 210, 240, 280 }, monsters.Select(monster => monster.Attack));
                Assert.All(monsters, monster => Assert.Equal(0, monster.Defense));
                Assert.All(waves, wave => Assert.Single(wave.Monsters));
                Assert.Equal("棱核守望者", monsters[^1].Name);
                Assert.True(bossProfile.UseEncounterLocalSkillClock);
                Assert.Equal(100, bossProfile.SkillUseChancePercent);
                Assert.Equal(new[] { "light-deep-lv1-warden-pierce", "light-deep-lv1-warden-nova",
                    "light-deep-lv1-warden-ward" }, bossProfile.Skills.Select(skill => skill.Code));
                Assert.DoesNotContain(bossProfile.Skills, skill => skill.Code == softEnrage.Code || skill.Code == hardEnrage.Code);
                var ward = monsterCombat.StatusEffects.Single(s => s.Code == "light-deep-lv1-ward");
                Assert.Equal(("ReductionPercent", 20m, 1, true, true),
                    (ward.EffectType, ward.ValuePerStack, ward.MaxStacks, ward.IsPositive, ward.IsDispellable));
                Assert.Equal("RandomAlive", monsterCombat.Skills.Single(s => s.Code == "light-deep-lv1-wraith-bolt").TargetType);
                Assert.Equal("RandomAlive", monsterCombat.Skills.Single(s => s.Code == "light-deep-lv1-conduit-beam").TargetType);
                Assert.Null(bossProfile.FireCore);
                Assert.Null(bossProfile.DeepCold);
                Assert.Null(bossProfile.EarthArmor);
                Assert.Null(bossProfile.StaticField);
            }
            else
            {
                Assert.Equal("plague-crypt-depths", dungeon.Code);
                Assert.Equal(new[] { 370000, 450000, 500000, 570000, 950000 }, monsters.Select(monster => monster.MaxHp));
                Assert.Equal(new[] { 145, 170, 195, 215, 230 }, monsters.Select(monster => monster.Attack));
                Assert.All(monsters, monster => Assert.Equal(0, monster.Defense));
                Assert.All(waves, wave => Assert.Single(wave.Monsters));
                Assert.Equal("缄丝巢主", monsters[^1].Name);
                Assert.True(bossProfile.UseEncounterLocalSkillClock);
                Assert.Equal(100, bossProfile.SkillUseChancePercent);
                Assert.Equal(new[] { "dark-deep-lv1-matriarch-fang", "dark-deep-lv1-matriarch-mist" },
                    bossProfile.Skills.Select(skill => skill.Code));
                var bossSkills = bossProfile.Skills.Select(entry => monsterCombat.Skills.Single(skill => skill.Code == entry.Code)).ToList();
                Assert.Equal(new[] { 1, 3 }, bossSkills.Select(skill => skill.InitialCooldownRounds));
                Assert.Equal(new[] { 3, 5 }, bossSkills.Select(skill => skill.CooldownRounds));
                Assert.True(bossSkills[0].IsInterruptible);
                Assert.False(bossSkills[1].IsInterruptible);
                Assert.All(bossSkills, skill =>
                {
                    Assert.Null(skill.SelfHpBelowPercent);
                    Assert.Null(skill.RoomRoundAtLeast);
                    Assert.Empty(skill.Statuses);
                    Assert.Equal(2, skill.Effects!.Count);
                    Assert.Equal(("dark-deep-lv1-poison", 20m, 2),
                        (skill.Effects[1].StatusCode, skill.Effects[1].AttackPowerPercent, skill.Effects[1].DurationRounds));
                });
                var poison = Assert.Single(monsterCombat.StatusEffects, s => s.Code == "dark-deep-lv1-poison");
                Assert.Equal(("DamageOverTime", 1, "RefreshDuration", true, false),
                    (poison.EffectType, poison.MaxStacks, poison.Stacking, poison.IsDispellable, poison.IsPositive));
                Assert.DoesNotContain(bossProfile.Skills, s => s.Code.StartsWith("endgame-") || s.Code.StartsWith("widow-"));
                Assert.Null(bossProfile.ReflectionMirror);
                Assert.Null(bossProfile.FireCore);
                Assert.Null(bossProfile.DeepCold);
                Assert.Null(bossProfile.EarthArmor);
                Assert.Null(bossProfile.StaticField);
            }

            var dungeonOffers = allOffers.Where(offer => offer.DungeonCode == dungeon.Code).ToList();
            var offers = dungeonOffers.Where(offer => offer.RewardKind == "Weapon").ToList();
            Assert.Equal(2, offers.Count);
            Assert.Equal(2, offers.Select(offer => offer.EffectiveRewardCode).Distinct().Count());
            Assert.All(offers, offer =>
            {
                Assert.Equal(60, offer.Cost);
                Assert.StartsWith("t1-deep-", offer.EffectiveRewardCode);
                Assert.Equal(dungeon.MonsterElement, catalog.FindItem(offer.EffectiveRewardCode)!.Element);
            });
            var fragmentOffer = Assert.Single(dungeonOffers, offer => offer.RewardKind == "Material");
            Assert.Equal((1, "weapon-fragment-t1", 5),
                (fragmentOffer.Cost, fragmentOffer.EffectiveRewardCode, fragmentOffer.RewardQuantity));
            var soulDefinition = Assert.Single(soulImprints, imprint => imprint.DungeonCode == dungeon.Code);
            var expectedSoul = expectedSoulImprints[dungeon.Code];
            Assert.Equal((expectedSoul.Element, expectedSoul.Effect, expectedSoul.Power, expectedSoul.Secondary,
                    expectedSoul.Duration, expectedSoul.InitialCooldown, expectedSoul.Cooldown,
                    expectedSoul.AutoCondition, expectedSoul.StatusCode),
                (soulDefinition.Element, soulDefinition.EffectType, soulDefinition.PowerPercent,
                    soulDefinition.SecondaryPowerPercent, soulDefinition.DurationRounds,
                    soulDefinition.InitialCooldownRounds, soulDefinition.CooldownRounds,
                    soulDefinition.AutoCondition, soulDefinition.StatusCode));
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
            Assert.DoesNotContain(rewards.DungeonClears[dungeon.Code].Drops, drop => drop.Kind == "Weapon");
            var clearFragments = Assert.Single(rewards.DungeonClears[dungeon.Code].Drops,
                drop => drop.Code == "weapon-fragment-t1");
            Assert.Equal(("Material", 5, 100m),
                (clearFragments.Kind, clearFragments.Quantity, clearFragments.ChancePercent));
            var clearToken = Assert.Single(rewards.DungeonClears[dungeon.Code].Drops,
                drop => drop.Code == fragmentOffer.CurrencyCode);
            Assert.Equal(("Material", 1, 100m), (clearToken.Kind, clearToken.Quantity, clearToken.ChancePercent));
            foreach (var monster in monsters)
            {
                var drops = rewards.MonsterKills[monster.RewardProfileCode].Drops
                    .Where(drop => drop.Kind == "Weapon").ToList();
                Assert.Equal(offers.Select(offer => offer.EffectiveRewardCode).Order(),
                    drops.Select(drop => drop.Code).Order());
                Assert.All(drops, drop =>
                {
                    Assert.Equal(1, drop.Quantity);
                    Assert.Equal(monster.IsBoss ? 5m : 1m, drop.ChancePercent);
                });
            }
            Assert.All(offers.Select(offer => catalog.FindItem(offer.EffectiveRewardCode)!), item =>
            {
                Assert.Equal(1, item.ItemLevel);
                Assert.All(item.Skills, skill => Assert.Equal(1, skill.Level));
                Assert.Contains(item.Attack + item.MaxHp, new[] { 240, 245 });
                Assert.Equal(3, item.Skills.Count);
                Assert.Equal(2, item.Skills.Count(skill => skill.UnlockQualityRank == 0));
                Assert.Equal(3, item.Skills.Single(skill => skill.UnlockQualityRank > 0).UnlockQualityRank);
            });
        }
        Assert.Equal(6, bossNames.Count);
        Assert.Equal(6, soulImprints.Select(imprint => imprint.Element).Distinct().Count());
        Assert.Equal(6, soulImprints.Select(imprint => imprint.EffectType).Distinct().Count());
        Assert.Equal(12, dungeons.SelectMany(dungeon => allOffers.Where(offer =>
            offer.DungeonCode == dungeon.Code && offer.RewardKind == "Weapon"))
            .Select(offer => offer.EffectiveRewardCode).Distinct().Count());
        Assert.Empty(dungeons.SelectMany(dungeon => rewards.DungeonClears[dungeon.Code].Drops
            .Where(drop => drop.Kind == "Weapon")));
    }

    [Fact]
    public async Task ExistingWeaponTemplatesRebaseAtomicallyAndPreserveInvestmentAndSlots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var weapon = new CharacterWeapon { Id = 1, CharacterId = 1, WeaponCode = "goldtooth-pickaxe", Name = "旧砾牙矿主矿镐",
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
        Assert.Equal(("weapon-might", 1, 0, 2, 3), Skill(rebased, 1));
        Assert.Equal(("weapon-skill", 1, 0, 1, 2), Skill(rebased, 2));
        Assert.Equal("t1-wood-hilt-ritual-dagger", mapped.WeaponCode);
        Assert.Equal(ElementType.Dark, mapped.Element);
        Assert.Equal(2, mapped.EquippedSlotIndex);
        Assert.Equal("weapon-stamina", Assert.Single(mapped.Skills).SkillCode);
        Assert.Equal((132, 165, 25), (character.Attack, character.MaxHp, character.Hp));
        Assert.Equal(174, TalentRules.EffectiveMaxHp(character));
        var version = rebased.Version;
        await DbInitializer.InitializeAsync(db, catalog);
        Assert.Equal(version, rebased.Version);
        Assert.Equal(3, await db.CharacterWeaponSkills.CountAsync());

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
        Assert.Equal(2, actual.TemplateRevision);
        Assert.Equal(3, actual.QualityRank);
        Assert.All(actual.Skills, skill => Assert.Equal(0, skill.QualityBonusLevel));
        Assert.Equal(1, actual.Skills[0].Level);
        Assert.Equal(2, actual.Skills.Count);
    }
}
