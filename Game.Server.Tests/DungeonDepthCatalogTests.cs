using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonDepthCatalogTests
{
    private static readonly string[] Codes = ["kobold-mine-depths", "plague-crypt-depths", "ragefire-heart",
        "frostspring-throne", "windfury-spire", "dawn-core"];

    [Fact]
    public void DepthUsesExplicitCodeAndRoundsOnlyFinalStat()
    {
        var catalog = CreateDepths();
        Assert.True(catalog.ValidateDepth(Codes[0].ToUpperInvariant(), 10));
        Assert.False(catalog.ValidateDepth(Codes[0], 11));
        Assert.False(catalog.ValidateDepth(Codes[0], 0));
        Assert.True(catalog.ValidateDepth("kobold-mine", 1));
        Assert.False(catalog.ValidateDepth("kobold-mine", 2));
        Assert.False(catalog.ValidateDepth("名字·深层LV1", 2));
        Assert.Equal(1.331m, catalog.StatMultiplier(Codes[0], 4));
        Assert.Equal(2, catalog.ScaleStat(1, 4, Codes[0]));
        Assert.Equal(134, catalog.ScaleStat(100, 4, Codes[0]));
        Assert.Equal(0, catalog.ScaleStat(0, 10, Codes[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.ScaleStat(100, 2, "kobold-mine"));
        Assert.Equal("烛井矿窟·深层LV7", catalog.DisplayName("烛井矿窟·深层LV1", 7));
        Assert.Equal("新副本·深层LV2", catalog.DisplayName("新副本", 2));
    }

    [Fact]
    public void ProductionConfigurationScalesAllSixEncountersAndKeepsLv4Mechanics()
    {
        var configuration = Configuration();
        var depths = CreateDepths(configuration);
        var combat = new MonsterCombatCatalog(Options.Create(Bind<MonsterCombatOptions>(configuration, MonsterCombatOptions.SectionName)));
        var encounters = new DungeonEncounterCatalog(Options.Create(Bind<DungeonEncounterOptions>(configuration,
            DungeonEncounterOptions.SectionName)), combat, depthCatalog: depths);
        foreach (var code in Codes)
        {
            var definition = depths.Find(code)!;
            Assert.Equal(1, definition.Stage);
            Assert.Equal(10m, definition.GoldBonusPercent);
            Assert.Equal(10m, definition.KillExtraRollChancePercent);
            Assert.Equal(10m, definition.ClearExtraRollChancePercent);
            Assert.Equal("weapon-breakthrough-fragment-t1", definition.ChallengeFragmentCode);
            var dungeon = new Dungeon { Code = code };
            var baseMonsters = encounters.CreateMonsters(dungeon);
            Assert.Equal(5, baseMonsters.Count);
            Assert.Equal(5, encounters.GetWaveCount(dungeon));
            for (var depth = 2; depth <= 10; depth++)
            {
                var monsters = encounters.CreateMonsters(dungeon, depth);
                for (var index = 0; index < monsters.Count; index++)
                {
                    var monster = monsters[index];
                    var original = baseMonsters[index];
                    var explicitFire = code == "ragefire-heart";
                    var explicitWater = code == "frostspring-throne";
                    var explicitEarth = code == "kobold-mine-depths";
                    var explicitWind = code == "windfury-spire";
                    var explicitLight = code == "dawn-core";
                    var explicitDark = code == "plague-crypt-depths";
                    int[] darkHp = depth == 2 ? [440000, 540000, 610000, 690000, 1150000] : depth == 3
                        ? [500000, 620000, 710000, 800000, 1350000] : [520000, 630000, 720000, 820000, 1400000];
                    int[] darkAttack = depth == 2 ? [160, 195, 225, 250, 265] : depth == 3
                        ? [185, 235, 270, 305, 310] : [205, 255, 295, 325, 330];
                    int[] lightHp = depth == 2 ? [430000, 520000, 610000, 680000, 1140000] : depth == 3
                        ? [500000, 600000, 700000, 790000, 1330000] : [510000, 610000, 710000, 800000, 1350000];
                    int[] lightAttack = depth == 2 ? [165, 205, 240, 275, 310] : depth == 3
                        ? [185, 230, 270, 310, 320] : [200, 245, 290, 320, 330];
                    int[] windHp = depth == 2 ? [440000, 530000, 600000, 680000, 1120000] : depth == 3
                        ? [490000, 600000, 690000, 770000, 1290000] : [550000, 650000, 750000, 850000, 1500000];
                    int[] windAttack = depth == 2 ? [165, 210, 235, 270, 300] : depth == 3
                        ? [195, 240, 280, 310, 325] : [215, 265, 310, 350, 390];
                    int[] fireHp = depth == 2 ? [450000, 520000, 600000, 670000, 1170000] : depth == 3
                        ? [495000, 575000, 690000, 735000, 1290000] : [500000, 580000, 700000, 740000, 1300000];
                    int[] fireAttack = depth == 2 ? [155, 185, 210, 235, 270] : depth == 3
                        ? [170, 205, 240, 270, 280] : [190, 230, 270, 310, 320];
                    int[] waterHp = depth == 2 ? [430000, 500000, 570000, 660000, 1100000] : depth == 3
                        ? [480000, 600000, 670000, 810000, 1320000] : [500000, 610000, 710000, 820000, 1390000];
                    int[] waterAttack = depth == 2 ? [165, 210, 235, 280, 320] : depth == 3
                        ? [180, 230, 265, 315, 360] : [210, 270, 315, 360, 420];
                    int[] earthHp = depth == 2 ? [450000, 550000, 620000, 690000, 1180000] : depth == 3
                        ? [500000, 600000, 680000, 770000, 1300000] : [530000, 630000, 730000, 820000, 1350000];
                    int[] earthAttack = depth == 2 ? [165, 210, 235, 280, 320] : depth == 3
                        ? [180, 230, 265, 300, 330] : [200, 250, 290, 325, 350];
                    var statBaseDepth = explicitDark || explicitFire || explicitWater || explicitEarth || explicitWind || explicitLight ? Math.Min(depth, 4) : 1;
                    Assert.Equal(depths.ScaleStat(explicitFire ? fireHp[index] : explicitWater ? waterHp[index] : explicitEarth ? earthHp[index] : explicitWind ? windHp[index] : explicitLight ? lightHp[index] : explicitDark ? darkHp[index] : original.MaxHp,
                        depth, code, statBaseDepth), monster.MaxHp);
                    Assert.Equal(monster.MaxHp, monster.Hp);
                    Assert.Equal(monster.MaxHp, monster.BaseMaxHp);
                    Assert.Equal(depths.ScaleAttack(explicitFire ? fireAttack[index] : explicitWater ? waterAttack[index] : explicitEarth ? earthAttack[index] : explicitWind ? windAttack[index] : explicitLight ? lightAttack[index] : explicitDark ? darkAttack[index] : original.Attack,
                        depth, code, statBaseDepth), monster.Attack);
                    Assert.Equal(original.Defense, monster.Defense);
                    Assert.Equal(original.RewardProfileCode, monster.RewardProfileCode);
                    Assert.Equal((original.WaveNumber, original.Position), (monster.WaveNumber, monster.Position));
                    if ((explicitFire || explicitWater || explicitEarth || explicitWind || explicitLight) && depth <= 4)
                    {
                        var prior = encounters.CreateMonsters(dungeon, depth - 1)[index];
                        Assert.True(monster.MaxHp > prior.MaxHp, $"{monster.Name}: LV{depth} HP must exceed LV{depth - 1}.");
                        Assert.True(monster.Attack > prior.Attack, $"{monster.Name}: LV{depth} attack must exceed LV{depth - 1}.");
                        Assert.False(definition.UsesPlaceholderAt(depth));
                    }
                    if (explicitDark && depth <= 4)
                    {
                        var prior = encounters.CreateMonsters(dungeon, depth - 1)[index];
                        Assert.True(monster.MaxHp > prior.MaxHp);
                        Assert.True(monster.Attack > prior.Attack);
                        Assert.False(definition.UsesPlaceholderAt(depth));
                    }
                    if (!monster.IsBoss)
                    {
                        Assert.Equal(original.CombatProfileCode, monster.CombatProfileCode);
                        continue;
                    }
                    var profile = combat.FindProfile(monster.CombatProfileCode)!;
                    var originalProfile = combat.FindProfile(original.CombatProfileCode)!;
                    if (code == "plague-crypt-depths")
                    {
                        Assert.Equal(new[] { 1, 2, 3, 4 }, definition.CalibratedDepths);
                        Assert.False(definition.UsesPlaceholderAt(depth));
                        Assert.True(profile.UseEncounterLocalSkillClock);
                        Assert.Equal(2, originalProfile.Skills.Count);
                        Assert.DoesNotContain(profile.Skills, s => s.Code.StartsWith("widow-") || s.Code.StartsWith("endgame-"));
                        Assert.Null(profile.FireCore);
                        Assert.Null(profile.DeepCold);
                        Assert.Null(profile.EarthArmor);
                        Assert.Null(profile.StaticField);
                        Assert.Null(profile.ReflectionMirror);
                        Assert.Null(originalProfile.PlaguePoison);
                        Assert.NotNull(profile.PlaguePoison);
                        Assert.Equal((depth < 4 ? 70 : (int?)null, 4, depth == 2 ? 2m : depth == 3 ? 4m : 6m, 10m, 3), (profile.PlaguePoison.TriggerHpPercent,
                            profile.PlaguePoison.WindowRounds, profile.PlaguePoison.BreakLightDamagePercent,
                            profile.PlaguePoison.AttackPercentPerStack, profile.PlaguePoison.RewardRounds));
                        Assert.Equal(depth == 2 ? 0 : 3, profile.PlaguePoison.ErosionStartStacks);
                        Assert.Equal(depth == 2 ? 0 : 5, profile.PlaguePoison.LethalStacks);
                        Assert.Equal(depth == 2 ? 0m : 10m, profile.PlaguePoison.ErosionPercentPerStack);
                        Assert.Equal(depth < 4 ? 0 : 6, profile.PlaguePoison.FirstActivationRound);
                        Assert.Equal(depth < 4 ? 0 : 10, profile.PlaguePoison.CycleRounds);
                        if (depth >= 3)
                            Assert.Contains("腐巢侵蚀", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        Assert.Equal(2, profile.Skills.Count);
                        Assert.DoesNotContain(profile.Skills,s => s.Code.StartsWith("depth-placeholder-"));
                        if(depth>=4)Assert.Contains("瘟疫循环",combat.GetAddedMechanics(original.CombatProfileCode!,depth));
                        Assert.Contains("疫巢剧毒", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        if (depth >= 5)
                            Assert.Equal(encounters.CreateMonsters(dungeon, 4)[index].CombatProfileCode, monster.CombatProfileCode);
                        continue;
                    }
                    if (code == "dawn-core")
                    {
                        Assert.Equal(new[] { 1, 2, 3, 4 }, definition.CalibratedDepths);
                        Assert.False(definition.UsesPlaceholderAt(depth));
                        Assert.True(profile.UseEncounterLocalSkillClock);
                        Assert.Null(profile.FireCore);
                        Assert.Null(profile.DeepCold);
                        Assert.Null(profile.EarthArmor);
                        Assert.Null(profile.StaticField);
                        Assert.NotNull(profile.ReflectionMirror);
                        Assert.Equal((depth < 4 ? 70 : (int?)null, 4, depth == 2 ? 3 : depth == 3 ? 4 : 6, 3), (profile.ReflectionMirror.TriggerHpPercent,
                            profile.ReflectionMirror.WindowRounds, profile.ReflectionMirror.InitialStacks, profile.ReflectionMirror.RewardRounds));
                        Assert.Equal(Game.Shared.Enums.ElementType.Dark, profile.ReflectionMirror.RemovalElement);
                        Assert.Equal(2m, profile.ReflectionMirror.ReflectPercentPerStack);
                        Assert.Equal(depth < 4 ? 5m : 3m, profile.ReflectionMirror.MaxHpCapPercentPerStack);
                        Assert.Equal(depth == 2 ? 0 : 3, profile.ReflectionMirror.GrowthRounds);
                        Assert.Equal(depth == 2 ? 0m : 0.5m, profile.ReflectionMirror.ReflectGrowthPercentPerStack);
                        Assert.Equal(depth == 2 ? 0m : 1m, profile.ReflectionMirror.MaxHpCapGrowthPercentPerStack);
                        Assert.Equal(depth == 2 ? string.Empty : depth == 3 ? "light-deep-lv3-amplification" : "light-deep-lv4-amplification", profile.ReflectionMirror.AmplificationStatusCode);
                        Assert.Equal(depth < 4 ? 0 : 6, profile.ReflectionMirror.FirstActivationRound);
                        Assert.Equal(depth < 4 ? 0 : 10, profile.ReflectionMirror.CycleRounds);
                        Assert.Contains(profile.Skills, entry => entry.Code == "light-deep-lv1-warden-pierce");
                        Assert.Contains(profile.Skills, entry => entry.Code == "light-deep-lv1-warden-nova");
                        Assert.DoesNotContain(profile.Skills, entry => entry.Code == "light-deep-lv1-warden-ward");
                        Assert.DoesNotContain(profile.Skills, entry => entry.Code.StartsWith("dawnwarden-") ||
                            entry.Code.StartsWith("endgame-"));
                        Assert.Equal(2, profile.Skills.Count);
                        Assert.Null(originalProfile.ReflectionMirror);
                        Assert.True(monster.MaxHp > original.MaxHp);
                        Assert.True(monster.Attack > original.Attack);
                        if (depth == 2)
                            Assert.Equal("琉辉反镜", Assert.Single(combat.GetAddedMechanics(original.CombatProfileCode!, depth)));
                        else
                            Assert.Contains("圣光增幅", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        if (depth >= 4)
                            Assert.Contains("黎明循环", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        if (depth >= 5)
                            Assert.Equal(encounters.CreateMonsters(dungeon, 4)[index].CombatProfileCode, monster.CombatProfileCode);
                        continue;
                    }
                    if (explicitWind)
                    {
                        Assert.Equal(new[] { 1, 2, 3, 4 }, definition.CalibratedDepths);
                        Assert.False(definition.UsesPlaceholderAt(depth));
                        Assert.NotNull(profile.StaticField);
                        Assert.Equal(Game.Shared.Enums.ElementType.Fire, profile.StaticField.RemovalElement);
                        Assert.Equal((depth < 4 ? 70 : (int?)null, 4, depth == 2 ? 2 : depth == 3 ? 3 : 4, 2, depth == 2 ? 1 : depth == 3 ? 2 : 3, 3), (profile.StaticField.TriggerHpPercent, profile.StaticField.WindowRounds,
                            profile.StaticField.InitialStacks, profile.StaticField.GrowthRounds, profile.StaticField.GrowthStacksPerRound,
                            profile.StaticField.RewardRounds));
                        Assert.Equal(5m, profile.StaticField.SkillDamagePercentPerStack);
                        Assert.Contains(profile.Skills, entry => entry.Code == "wind-deep-lv1-matriarch-dive");
                        Assert.Contains(profile.Skills, entry => entry.Code == "wind-deep-lv1-matriarch-tempest");
                        Assert.DoesNotContain(profile.Skills, entry => entry.Code == "wind-deep-lv1-matriarch-song");
                        Assert.DoesNotContain(profile.Skills, entry => entry.Code.StartsWith("depth-placeholder-"));
                        Assert.Equal(depth < 4 ? 0 : 6, profile.StaticField.FirstActivationRound);
                        Assert.Equal(depth < 4 ? 0 : 10, profile.StaticField.CycleRounds);
                        if (depth >= 4) Assert.Contains("风暴循环", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        Assert.Contains("静电领域", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        Assert.Equal(depth == 2 ? 0 : 5, profile.StaticField.ThunderAtStacks);
                        if (depth >= 3)
                        {
                            Assert.Contains(profile.Skills, entry => entry.Code == "wind-deep-lv3-thunder");
                            Assert.Contains("雷暴共鸣", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                            Assert.Equal(150, combat.FindSkill("wind-deep-lv3-thunder")!.DamagePowerPercent);
                        }
                        if (depth == 2)
                        {
                            Assert.True(monster.MaxHp > original.MaxHp);
                            Assert.True(monster.Attack > original.Attack);
                            Assert.Equal("静电领域", Assert.Single(combat.GetAddedMechanics(original.CombatProfileCode!, depth)));
                        }
                        if (depth >= 5)
                            Assert.Equal(encounters.CreateMonsters(dungeon, 4)[index].CombatProfileCode, monster.CombatProfileCode);
                        continue;
                    }
                    if (explicitEarth)
                    {
                        Assert.NotNull(profile.EarthArmor);
                        Assert.Null(profile.FireCore);
                        Assert.Null(profile.DeepCold);
                        Assert.Equal((depth <= 3 ? 70 : (int?)null, 5, depth == 2 ? 2m : depth == 3 ? 4m : 6m, 3), (profile.EarthArmor.TriggerHpPercent, profile.EarthArmor.WindowRounds,
                            profile.EarthArmor.BreakWindDamagePercent, profile.EarthArmor.RewardRounds));
                        Assert.Equal(depth < 4 ? 0 : 6, profile.EarthArmor.FirstActivationRound);
                        Assert.Equal(depth < 4 ? 0 : 8, profile.EarthArmor.CycleRounds);
                        Assert.Equal(depth == 2 ? string.Empty : "earth-deep-lv3-resonance", profile.EarthArmor.ResonanceStatusCode);
                        Assert.Contains(profile.Skills, entry => entry.Code == "earth-deep-lv1-overseer-pick");
                        Assert.Contains(profile.Skills, entry => entry.Code == "earth-deep-lv1-overseer-rockfall");
                        Assert.DoesNotContain(profile.Skills, entry => entry.Code == "earth-deep-lv1-overseer-armor");
                        Assert.DoesNotContain(profile.Skills, entry => entry.Code.StartsWith("depth-placeholder-"));
                        Assert.False(definition.UsesPlaceholderAt(depth));
                        if (depth == 2)
                        {
                            Assert.True(monster.MaxHp > original.MaxHp);
                            Assert.True(monster.Attack > original.Attack);
                            Assert.Equal("矿脉护甲", Assert.Single(combat.GetAddedMechanics(original.CombatProfileCode!, depth)));
                        }
                        if (depth == 3)
                            Assert.Equal(new[] { "矿脉护甲", "地脉共鸣" }, combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        if (depth >= 4)
                            Assert.Equal(new[] { "矿脉护甲", "地脉共鸣", "深岩循环" }, combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        if (depth >= 5)
                            Assert.Equal(encounters.CreateMonsters(dungeon, 4)[index].CombatProfileCode, monster.CombatProfileCode);
                        continue;
                    }
                    if (explicitFire)
                    {
                        Assert.Equal(originalProfile.Skills.Count, profile.Skills.Count);
                        Assert.All(originalProfile.Skills, skill => Assert.Contains(profile.Skills, entry => entry.Code == skill.Code));
                        Assert.DoesNotContain(profile.Skills, entry => entry.Code.StartsWith("depth-placeholder-"));
                        Assert.NotNull(profile.FireCore);
                        Assert.Equal(depth == 2 ? 2.5m : depth == 3 ? 5m : 7m, profile.FireCore.BreakWaterDamagePercent);
                        Assert.Equal(4, profile.FireCore.WindowRounds);
                        if (depth <= 3)
                        {
                            Assert.Equal(65, profile.FireCore.TriggerHpPercent);
                            Assert.Equal(0, profile.FireCore.CycleRounds);
                            Assert.Equal(depth == 2 ? 0 : 2, profile.FireCore.ExtraTargetCount);
                            Assert.Equal(depth == 2 ? 1 : 2, combat.GetAddedMechanics(original.CombatProfileCode!, depth).Count);
                            Assert.DoesNotContain("火山循环", combat.GetAddedMechanics(original.CombatProfileCode!, depth));
                        }
                        else
                            Assert.Equal(encounters.CreateMonsters(dungeon, 4)[index].CombatProfileCode, monster.CombatProfileCode);
                        continue;
                    }
                    if (explicitWater)
                    {
                        Assert.NotNull(profile.DeepCold);
                        Assert.Equal(depth <= 3 ? 70 : (int?)null, profile.DeepCold.TriggerHpPercent);
                        Assert.Equal(4, profile.DeepCold.WindowRounds);
                        Assert.Equal(depth == 2 ? 3 : 4, profile.DeepCold.InitialStacks);
                        Assert.Equal(depth == 2 ? 0 : depth == 3 ? 1 : 2, profile.DeepCold.GrowthStacksPerRound);
                        Assert.Equal(depth == 2 ? 0 : 5, profile.DeepCold.FreezeAtStacks);
                        Assert.False(definition.UsesPlaceholderAt(depth));
                        Assert.Equal(depth <= 3 ? 0 : 6, profile.DeepCold.FirstActivationRound);
                        Assert.Equal(depth <= 3 ? 0 : 10, profile.DeepCold.CycleRounds);
                        Assert.Equal(Math.Min(depth, 4), statBaseDepth);
                    }
                    var placeholderCount = explicitWater ? 0 : Math.Max(0, Math.Min(depth, 4) - 1);
                    Assert.Equal(originalProfile.Skills.Count + placeholderCount, profile.Skills.Count);
                    Assert.All(originalProfile.Skills, skill => Assert.Contains(profile.Skills, entry => entry.Code == skill.Code));
                    var placeholders = profile.Skills.Where(skill => skill.Code.StartsWith("depth-placeholder-")).ToList();
                    Assert.Equal(placeholderCount, placeholders.Count);
                    Assert.All(placeholders, entry =>
                    {
                        var skill = combat.FindSkill(entry.Code)!;
                        Assert.Equal(101, skill.DamagePowerPercent);
                        Assert.Contains("尚未实现", skill.Description);
                    });
                    Assert.Equal(placeholders.Count, placeholders.Select(entry => combat.FindSkill(entry.Code)!.Name).Distinct().Count());
                    if (depth >= 5)
                        Assert.Equal(encounters.CreateMonsters(dungeon, 4)[index].CombatProfileCode, monster.CombatProfileCode);
                }
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => encounters.CreateMonsters(new Dungeon { Code = "kobold-mine" }, 2));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    public async Task LoadedPlaceholdersActuallyExecuteAndCycleWithCooldown(int depth, int expectedCount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var combat = new MonsterCombatCatalog(Options.Create(DepthMechanicTestFactory.WithCurrentDeclarations(new MonsterCombatOptions
        {
            Profiles = new() { ["base"] = new() { SkillUseChancePercent = 0 } }
        })));
        var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 1 };
        var monster = new Monster { Id = 1, RoomId = 1, Name = "Boss", Hp = 100, MaxHp = 100, Attack = 10,
            CombatProfileCode = combat.ResolveDepthProfile("base", depth) };
        var character = new Character { Id = 1, UserId = 1, Name = "Player", Hp = 1000, MaxHp = 1000 };
        var slot = new RoomSlot { Id = 1, RoomId = 1, UserId = 1, CharacterId = 1, SlotIndex = 1 };
        db.AddRange(room, monster, character, slot);
        await db.SaveChangesAsync();
        var service = new MonsterCombatService(db, combat);
        for (var index = 0; index < expectedCount; index++)
        {
            room.RoundNumber = index;
            var intent = await service.EnsureIntentAsync(room, monster);
            Assert.Equal($"depth-placeholder-lv{index + 2}", intent.SkillCode);
            var previousHp = character.Hp;
            var logs = new List<string>();
            await service.ExecuteIntentAsync(room, monster, [new(slot, character)], new Dictionary<int, Game.Shared.Enums.ElementType>(), logs);
            await db.SaveChangesAsync();
            Assert.True(character.Hp < previousHp);
            Assert.Contains(logs, log => log.Contains(combat.FindSkill(intent.SkillCode)!.Name));
        }
        room.RoundNumber = 4;
        Assert.Equal("depth-placeholder-lv2", (await service.EnsureIntentAsync(room, monster)).SkillCode);
    }

    [Fact]
    public void InvalidConfigurationIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["bad"] = new() { ChallengeFragmentCode = "fragment", KillExtraRollChancePercent = 101 } }
        })));
    }

    [Fact]
    public void MaximumDepthCannotCreateAnUnboundedPreviewList()
    {
        Assert.Throws<InvalidOperationException>(() => new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["unbounded"] = new() { ChallengeFragmentCode = "fragment", MaximumDepth = int.MaxValue, GrowthPercent = 0 } }
        })));
    }

    [Fact]
    public void UnsafeMaximumMultiplierIsRejectedDuringConfigurationLoading()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["overflow"] = new() { ChallengeFragmentCode = "fragment", MaximumDepth = 100, GrowthPercent = 100 } }
        })));
        Assert.Contains("overflow", exception.Message);
        Assert.Contains("LV100", exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    public void UnsafeMonsterStatIsRejectedWhenEncounterCatalogLoads(int hp, int attack)
    {
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["overflow"] = new() { ChallengeFragmentCode = "fragment", MaximumDepth = 2 } }
        }));
        var exception = Assert.Throws<InvalidOperationException>(() => new DungeonEncounterCatalog(Options.Create(new DungeonEncounterOptions
        {
            Dungeons = new() { ["overflow"] = [new() { Monsters = [new() { Name = "Huge", MaxHp = hp, Attack = attack }] }] }
        }), depthCatalog: depths));
        Assert.Contains("overflow", exception.Message);
        Assert.Contains("Huge", exception.Message);
        Assert.Contains("LV2", exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public void DepthConfigurationCannotBypassStatValidationThroughLegacyEncounterFallback()
    {
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["missing"] = new() { ChallengeFragmentCode = "fragment" } }
        }));
        var exception = Assert.Throws<InvalidOperationException>(() => new DungeonEncounterCatalog(
            Options.Create(new DungeonEncounterOptions()), depthCatalog: depths));
        Assert.Contains("missing", exception.Message);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        TestRepository.File("Game.Server", "appsettings.json"))).Build();

    private static T Bind<T>(IConfiguration configuration, string section) where T : new()
    {
        var options = new T();
        configuration.GetSection(section).Bind(options);
        return options;
    }

    private static DungeonDepthCatalog CreateDepths(IConfiguration? configuration = null) =>
        new(Options.Create(Bind<DungeonDepthOptions>(configuration ?? Configuration(), DungeonDepthOptions.SectionName)));
}
