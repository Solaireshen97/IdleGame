using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Shop;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WorldContentTests
{
    [Fact]
    public void ProfessionProgressionConfigurationDefinesBothTreesAndRareBonusMaterials()
    {
        var content = new Content();
        var professions = new ProfessionCatalog(content.Bind<ProfessionProgressionOptions>(ProfessionProgressionOptions.SectionName));
        Assert.Equal(30, professions.ExperienceToNextLevel(1));
        Assert.Null(professions.ExperienceToNextLevel(10));
        Assert.Equal(3, content.Bind<ProfessionProgressionOptions>(ProfessionProgressionOptions.SectionName).Value.TalentNodes.Count(node =>
            node.ProfessionCode == ProfessionCatalog.GatheringCode));
        Assert.Equal(3, content.Bind<ProfessionProgressionOptions>(ProfessionProgressionOptions.SectionName).Value.TalentNodes.Count(node =>
            node.ProfessionCode == ProfessionCatalog.AlchemyCode));
        var gathering = new GatheringCatalog(content.Bind<GatheringOptions>(GatheringOptions.SectionName),
            content.World, content.Materials);
        Assert.All(gathering.Points.Where(point => point.IsRare), point => Assert.NotNull(point.BonusMaterialCode));
    }

    [Fact]
    public void ProductionRecipeUsesGatheredHerbsAndCharacterConsumable()
    {
        var content = new Content();
        var production = new ProductionCatalog(content.Bind<ProductionOptions>(ProductionOptions.SectionName),
            content.World, content.Materials, content.Consumables);
        var recipe = production.Recipes.Single(item => item.Code == "minor-healing-potion");
        Assert.Equal("minor-healing-potion", recipe.OutputCode);
        Assert.Equal(10, recipe.CycleSeconds);
        Assert.Equal("northshire-wolves", recipe.UnlockTargetCode);
        Assert.Equal(5, recipe.AlternativeUnlockTargetCodes.Count);
        Assert.Equal(("peacebloom", 2), (Assert.Single(recipe.Ingredients).Code, recipe.Ingredients[0].Quantity));
        Assert.Single(production.Recipes, item => item.OutputCode == "minor-healing-potion");
        var whetstone = Assert.Single(production.Recipes, item => item.OutputCode == "whetstone-oil");
        Assert.Equal("peacebloom", Assert.Single(whetstone.Ingredients).Code);
        Assert.DoesNotContain(production.Recipes, item => item.OutputCode == "travel-healing-potion");
        Assert.DoesNotContain(production.Recipes, item => item.Code == "elwynn-assault-legacy-batch");
        Assert.Equal(2, content.Consumables.Items.Count(item => item.Kind == "Healing"));
        var operationPotion = production.Recipes.Single(item => item.Code == "northshire-battle-draught");
        Assert.Equal(("peacebloom", 3), (Assert.Single(operationPotion.Ingredients).Code, operationPotion.Ingredients[0].Quantity));
    }

    [Fact]
    public void ProductionRareGatheringPointsRequireEliteVictoriesAndConfiguredMaterials()
    {
        var content = new Content();
        var gathering = new GatheringCatalog(content.Bind<GatheringOptions>(GatheringOptions.SectionName),
            content.World, content.Materials);
        var rarePoints = gathering.Points.Where(point => point.IsRare).ToList();
        Assert.Equal(6, rarePoints.Count);
        Assert.Equal(19, gathering.Points.Count);
        var common = Assert.Single(gathering.Points, point => point.MaterialCode == "peacebloom");
        Assert.Equal(GatheringCatalog.GlobalRegionCode, common.RegionCode);
        Assert.Equal(5, common.AlternativeUnlockTargetCodes.Count);
        Assert.All(new[] { "tirisfal-gravemoss", "durotar-aloe", "dun-morogh-frostdew",
            "mulgore-sage", "eversong-goldleaf" }, code => Assert.NotNull(content.Materials.FindItem(code)));
        foreach (var region in content.World.Regions)
        {
            var points = gathering.Points.Where(point => point.RegionCode == region.Code).ToList();
            Assert.Equal(3, points.Count);
            Assert.Single(points, point => point.IsRare);
            Assert.Single(points, point => point.UnlockKind == "DungeonClear" && point.OutputQuantity == 2);
            Assert.All(points, point => Assert.Equal(20, point.CycleSeconds));
        }
        Assert.All(rarePoints, point =>
        {
            Assert.Equal("Dungeon", content.World.Dungeons.Single(dungeon =>
                dungeon.Code == point.UnlockTargetCode).DungeonKind);
            Assert.Equal("DungeonClear", point.UnlockKind);
            Assert.NotNull(content.Materials.FindItem(point.MaterialCode));
            Assert.Equal(20, point.CycleSeconds);
        });
        var production = new ProductionCatalog(content.Bind<ProductionOptions>(ProductionOptions.SectionName),
            content.World, content.Materials, content.Consumables);
        var gatheredMaterials = gathering.Points.Select(point => point.MaterialCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(15, production.Recipes.Count);
        Assert.All(production.Recipes, recipe =>
            Assert.All(recipe.Ingredients, ingredient => Assert.Contains(ingredient.Code, gatheredMaterials)));
        foreach (var rare in rarePoints)
        {
            var batches = production.Recipes.Where(recipe => recipe.Ingredients.Any(ingredient =>
                ingredient.Code == rare.MaterialCode)).ToList();
            var batch = Assert.Single(batches);
            Assert.Equal(1, batch.OutputQuantity);
            Assert.StartsWith("greater-", batch.OutputCode);
            Assert.Equal(10, batch.CycleSeconds);
        }
    }

    [Fact]
    public void ProductionWorldHasSixCompleteRegionsWithEntryPreparationAndIndependentDeepExchanges()
    {
        var content = new Content();
        content.World.ValidateContent(content.Weapons, content.Encounters, content.Rewards, content.Exchanges);
        Assert.Equal(6, content.World.Regions.Count);
        Assert.Equal(6, content.World.Regions.Select(region => region.FeaturedElement).Distinct().Count());
        Assert.Equal(new[] { ElementType.Fire, ElementType.Water, ElementType.Earth,
            ElementType.Wind, ElementType.Light, ElementType.Dark },
            content.World.Regions.Select(region => region.FeaturedElement));
        Assert.Equal(6, content.Exchanges.Offers.Select(offer => offer.CurrencyCode).Distinct().Count());
        var weaponOffers = content.Exchanges.Offers.Where(offer => offer.RewardKind == "Weapon").ToList();
        Assert.Equal(12, weaponOffers.Select(offer => offer.EffectiveRewardCode).Distinct().Count());
        Assert.Equal(12, weaponOffers.Select(offer => content.Weapons.FindItem(offer.EffectiveRewardCode)!.Name).Distinct().Count());
        var depthDefinitions = content.Bind<DungeonDepthOptions>(DungeonDepthOptions.SectionName).Value.Dungeons;
        Assert.Equal(6, depthDefinitions.Count);

        foreach (var region in content.World.Regions)
        {
            var dungeons = content.World.Dungeons.Where(dungeon => dungeon.RegionCode == region.Code).ToList();
            Assert.Equal((1, 10), (region.MinimumLevel, region.MaximumLevel));
            Assert.Equal(Enumerable.Range(1, 10), dungeons.Select(dungeon => dungeon.MinimumLevel).Distinct().Order());
            Assert.Equal(11, dungeons.Count);
            Assert.Equal(7, dungeons.Count(dungeon => dungeon.DungeonKind == "Hunt"));
            Assert.Equal(2, dungeons.Count(dungeon => dungeon.DungeonKind == "Elite"));
            Assert.Equal(2, dungeons.Count(dungeon => dungeon.DungeonKind == "Dungeon"));
            var visible = dungeons.Where(item => item.IsVisible).ToList();
            Assert.Equal(5, visible.Count);
            Assert.Equal([1, 3, 5], visible.Where(item => item.DungeonKind == "Hunt")
                .Select(item => item.MinimumLevel).Order().ToArray());
            Assert.Equal(2, visible.Count(item => item.DungeonKind == "Dungeon"));
            Assert.DoesNotContain(visible, item => item.DungeonKind == "Elite");
            Assert.All(dungeons, challenge =>
            {
                Assert.Equal(region.FeaturedElement, challenge.MonsterElement);
                Assert.All(content.Encounters.CreateMonsters(challenge), monster =>
                    Assert.Equal(region.FeaturedElement, monster.Element));
            });
            var dungeon = Assert.Single(dungeons, dungeon => dungeon.Code == region.FeaturedDungeonCode);
            var deep = Assert.Single(visible, item => item.DungeonKind == "Dungeon" && item.Code != dungeon.Code);
            Assert.Equal(dungeon.Code, depthDefinitions[deep.Code].PrerequisiteDungeonCode);
            Assert.Equal((8, 10), (dungeon.MinimumLevel, dungeon.RecommendedLevel));
            Assert.Equal(region.FeaturedDungeonCode, dungeon.Code);
            Assert.Equal(4, content.Encounters.GetWaveCount(dungeon));
            var monsters = content.Encounters.CreateMonsters(dungeon);
            Assert.Equal(4, monsters.Count);
            var boss = Assert.Single(monsters, monster => monster.IsBoss);
            Assert.Same(monsters.Last(), boss);
            Assert.Equal(region.FeaturedElement, boss.Element);
            Assert.InRange(content.Combat.FindProfile(boss.CombatProfileCode)!.Skills.Count, 3, 4);

            var bossWeapon = content.Weapons.FindItem(region.FeaturedWeaponCode)!;
            Assert.Equal(region.FeaturedElement, bossWeapon.Element);
            Assert.Empty(content.RewardOptions.MonsterKills[boss.RewardProfileCode].Drops.Where(drop => drop.Kind == "Weapon"));
            Assert.DoesNotContain(content.RewardOptions.DungeonClears.Values, bundle => bundle.Drops.Any(drop => drop.Code == region.FeaturedWeaponCode));

            var allOffers = content.Exchanges.Offers.Where(offer => offer.DungeonCode == dungeon.Code).ToList();
            var offers = allOffers.Where(offer => offer.RewardKind == "Weapon").ToList();
            Assert.Empty(offers);
            Assert.Empty(allOffers);
            var repeatDrop = Assert.Single(content.RewardOptions.DungeonClears[dungeon.Code].Drops, drop => drop.Kind == "Material");
            Assert.Equal(("weapon-fragment-t1", 12, 100m), (repeatDrop.Code, repeatDrop.Quantity, repeatDrop.ChancePercent));
            var firstDrop = Assert.Single(content.RewardOptions.DungeonClears[$"{dungeon.Code}-first-clear"].Drops,
                drop => drop.Kind == "Material");
            Assert.Equal(("weapon-fragment-t1", 20, 100m), (firstDrop.Code, firstDrop.Quantity, firstDrop.ChancePercent));
        }
    }

    [Theory]
    [InlineData("kobold-mine")]
    [InlineData("spider-canyon")]
    [InlineData("ragefire-chasm")]
    [InlineData("frostspring-cavern")]
    [InlineData("windfury-nest")]
    [InlineData("dawn-ruins")]
    public async Task OrdinaryDungeonAllowsLowLevelsAndSettlesSharedMaterialsOnce(string dungeonCode)
    {
        var content = new Content();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(db, content.World);
        var character = new Character { Id = 1, UserId = 1, Name = "主控", Level = 1, Hp = 100, MaxHp = 100, Attack = 20 };
        var alternate = new Character { Id = 2, UserId = 1, Name = "替补", Level = 1, Hp = 100, MaxHp = 100, Attack = 20 };
        var guest = new Character { Id = 3, UserId = 2, Name = "访客", Level = 1, Hp = 100, MaxHp = 100, Attack = 20 };
        db.AddRange(character, alternate, guest,
            new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 3 },
            new UserLoginSession { UserId = 1, Token = "owner-token", ExpireAt = DateTime.UtcNow.AddDays(1) },
            new UserLoginSession { UserId = 2, Token = "guest-token", ExpireAt = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var progression = new ProgressionService(content.Bind<ProgressionOptions>(ProgressionOptions.SectionName));
        var skills = new SkillCatalog(content.Bind<SkillOptions>(SkillOptions.SectionName));
        var users = new UserService(db, progression, skills);
        var rewards = new RewardService(db, content.Rewards, progression);
        var rooms = new RoomService(db, users, progression, content.Consumables, skills, rewards,
            content.Encounters, worldCatalog: content.World);
        var dungeon = await db.Dungeons.SingleAsync(item => item.Code == dungeonCode);
        var created = await rooms.CreateRoomAsync(dungeon.Id, null, "owner-token", isPreparationTimeoutEnabled: false, isPublic: true);
        Assert.Null(created.Error);
        Assert.NotNull(created.Detail);
        Assert.Equal(dungeon.RegionName, created.Detail.RegionName);
        Assert.Null((await rooms.JoinRoomAsync(created.Detail.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "guest-token")).Error);
        Assert.Null((await rooms.AssignSlotAsync(created.Detail.RoomId,
            new AssignRoomSlotRequest { CharacterId = alternate.Id, SlotIndex = 3 }, "owner-token")).Error);
        Assert.DoesNotContain(content.Exchanges.Offers, offer => offer.DungeonCode == dungeonCode);

        var room = await db.Rooms.SingleAsync();
        var runService = new DungeonRunService(db, rewards);
        var monsters = await db.Monsters.Where(monster => monster.RoomId == room.Id)
            .OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).ToListAsync();
        for (var run = 1; run <= 2; run++)
        {
            if (run > 1) { room.RunSequence++; await runService.ResetEncounterAsync(room); }
            foreach (var monster in monsters)
            {
                monster.Hp = 0;
                var advanced = await runService.AdvanceAfterDefeatAsync(room, monster,
                    [new RewardParticipant(1, character)], DateTime.UtcNow, []);
                Assert.Null(advanced.Error);
                Assert.Equal(monster == monsters.Last(), advanced.IsDungeonComplete);
                await db.SaveChangesAsync();
            }
            var fragments = await db.CharacterItemStacks.SingleAsync(stack =>
                stack.CharacterId == character.Id && stack.ItemCode == "weapon-fragment-t1");
            Assert.Equal(20 + run * 12, fragments.Quantity);
            await rewards.SettleAsync(room, true, DateTime.UtcNow, []);
            await db.SaveChangesAsync();
            Assert.Equal(20 + run * 12, fragments.Quantity);
        }
        Assert.Single(await db.UserDungeonClears.ToListAsync());
        Assert.True(character.Gold > 0);
        Assert.DoesNotContain(await db.RewardEntries.ToListAsync(), entry => entry.Code.EndsWith("-token"));
        var allowedWeapons = content.RewardOptions.DungeonClears[dungeonCode].Drops
            .Where(drop => drop.Kind == "Weapon").Select(drop => drop.Code).ToArray();
        Assert.All(await db.RewardEntries.Where(entry => entry.Kind == "Weapon").ToListAsync(),
            entry => Assert.Contains(entry.Code, allowedWeapons));
    }

    [Fact]
    public async Task MigrationAndRepeatedSeedingPreserveExistingRoomsAndClears()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260921100000_AddDungeonContentProgression");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Dungeons (Id,Code,Name,MonsterName,MonsterMaxHp,MonsterAttack,MonsterDefense,MonsterElement,SlotCount,SortOrder,RegionName,DungeonKind,Description,MinimumLevel,RecommendedLevel,IsVisible) VALUES (55,'kobold-mine','烛井矿窟','砾牙矿主',140,15,5,'Earth',5,8,'岩芽林地','Dungeon','旧描述',8,8,1)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Monsters (Id,Name,Element,Hp,MaxHp,Attack,Defense,RoomId,WaveNumber,Position,CombatProfileCode,RewardProfileCode,IsBoss) VALUES (7,'砾牙矿主','Earth',41,140,15,5,9,4,1,'','',1)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Rooms (Id,DungeonId,MonsterId,OwnerUserId,SlotCount,Status,IsPreparationTimeoutEnabled,IsRepeatBattle,RoundNumber,RunSequence,Version,CurrentWaveNumber,TotalWaveCount) VALUES (9,55,7,1,5,0,1,0,0,1,0,4,4)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO UserDungeonClears (UserId,DungeonId,ClearedAtUtc) VALUES (1,55,'2026-09-21 00:00:00')");
        var content = new Content();
        var world = content.World;
        await DbInitializer.InitializeAsync(db, world: world, encounters: content.Encounters);
        await DbInitializer.InitializeAsync(db, world: world);
        Assert.Equal(69, await db.Dungeons.CountAsync());
        Assert.Equal(55, (await db.Dungeons.SingleAsync(dungeon => dungeon.Code == "kobold-mine")).Id);
        Assert.Equal("elwynn", (await db.Dungeons.FindAsync(55))!.RegionCode);
        Assert.Equal(55, (await db.Rooms.SingleAsync()).DungeonId);
        Assert.Equal(41, (await db.Monsters.SingleAsync()).Hp);
        Assert.Equal(55, (await db.UserDungeonClears.SingleAsync()).DungeonId);
        Assert.All(world.Dungeons, dungeon => Assert.Equal(0, dungeon.Id));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void WorldRejectsChallengeSummaryWithDifferentRegionalElement()
    {
        var content = new Content();
        content.World.Dungeons.Single(dungeon => dungeon.Code == "northshire-wolves").MonsterElement = ElementType.Wind;
        Assert.Throws<InvalidOperationException>(() => new WorldCatalog(Options.Create(new WorldOptions
        {
            Regions = content.World.Regions.ToList(), Dungeons = content.World.Dungeons.ToList()
        })));
    }

    [Fact]
    public void WorldRejectsNonBossEncounterWithDifferentRegionalElement()
    {
        var content = new Content();
        var options = content.Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName);
        options.Value.Dungeons["kobold-mine"][0].Monsters[0].Element = ElementType.Fire;
        var encounters = new DungeonEncounterCatalog(options, content.Combat, content.Rewards);
        var error = Assert.Throws<InvalidOperationException>(() => content.World.ValidateContent(
            content.Weapons, encounters, content.Rewards, content.Exchanges));
        Assert.Contains("kobold-mine", error.Message);
    }

    [Fact]
    public async Task StartupUnifiesSavedRegionalMonstersWithoutResettingBattleOrRewards()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        var content = new Content();
        var world = content.World;
        await DbInitializer.InitializeAsync(db, world: world, encounters: content.Encounters);
        var dungeon = await db.Dungeons.SingleAsync(item => item.Code == "kobold-mine");
        var hiddenDungeon = await db.Dungeons.SingleAsync(item => item.Code == "slime-field");
        var room = new Room { Id = 101, DungeonId = dungeon.Id, MonsterId = 202, Status = RoomStatus.Preparing,
            CurrentWaveNumber = 2, TotalWaveCount = 4, RoundNumber = 7, RunSequence = 3,
            ScalingPartySize = 2, Version = 9, IsRepeatBattle = true };
        var oldRoom = new Room { Id = 102, DungeonId = dungeon.Id, MonsterId = 204 };
        var hiddenRoom = new Room { Id = 103, DungeonId = hiddenDungeon.Id, MonsterId = 205 };
        db.Rooms.AddRange(room, oldRoom, hiddenRoom);
        db.Monsters.AddRange(
            new Monster { Id = 201, RoomId = room.Id, Name = "狗头人矿工", Element = ElementType.Fire,
                WaveNumber = 1, Position = 1, Hp = 0, BaseMaxHp = 50, MaxHp = 90 },
            new Monster { Id = 202, RoomId = room.Id, Name = "狗头人掘地工", Element = ElementType.Wind,
                WaveNumber = 2, Position = 1, Hp = 41, BaseMaxHp = 140, MaxHp = 252,
                Attack = 15, Defense = 5, CombatProfileCode = "saved-profile", RewardProfileCode = "saved-rewards" },
            new Monster { Id = 203, RoomId = room.Id, Name = "金牙", Element = ElementType.Earth,
                WaveNumber = 4, Position = 1, Hp = 252, BaseMaxHp = 140, MaxHp = 252, IsBoss = true },
            new Monster { Id = 204, Name = "金牙", Element = ElementType.Dark, Hp = 21, MaxHp = 50 },
            new Monster { Id = 205, RoomId = hiddenRoom.Id, Name = "隐藏旧内容", Element = ElementType.Water, Hp = 31, MaxHp = 50 },
            new Monster { Id = 206, Name = "未关联怪物", Element = ElementType.Light, Hp = 11, MaxHp = 50 });
        var intent = new MonsterIntent { RoomId = room.Id, RunSequence = 3, RoundNumber = 7,
            MonsterId = room.MonsterId, ActionType = "Skill", SkillCode = "saved-skill" };
        db.MonsterIntents.Add(intent);
        db.UserDungeonClears.Add(new UserDungeonClear { UserId = 1, DungeonId = dungeon.Id });
        db.RewardRuns.Add(new RewardRun { RoomId = room.Id, Sequence = 3 });
        db.RewardEvents.Add(new RewardEvent { RoomId = room.Id, Sequence = 3, EventKey = "monster:1:1" });
        db.RewardEntries.Add(new RewardEntry { RoomId = room.Id, Sequence = 3, EventKey = "monster:1:1",
            UserId = 1, CharacterId = 1, Kind = "Gold", Quantity = 12 });
        await db.SaveChangesAsync();

        await DbInitializer.InitializeAsync(db, world: world, encounters: content.Encounters);
        await DbInitializer.InitializeAsync(db, world: world, encounters: content.Encounters);
        db.ChangeTracker.Clear();
        var monsters = await db.Monsters.OrderBy(monster => monster.Id).ToListAsync();
        Assert.Equal(new[] { ElementType.Earth, ElementType.Earth, ElementType.Earth,
            ElementType.Earth, ElementType.Water, ElementType.Light }, monsters.Select(monster => monster.Element));
        Assert.Equal(new[] { 0, 41, 252, 21, 31, 11 }, monsters.Select(monster => monster.Hp));
        Assert.Equal(new[] { "岩芽幼狼", "岩牙野猪", "砾牙矿主", "砾牙矿主", "隐藏旧内容", "未关联怪物" },
            monsters.Select(monster => monster.Name));
        Assert.Equal("/art/monsters/monster-005.png", Game.Client.Services.MonsterArt.ForName(monsters[0].Name));
        Assert.Equal("/art/monsters/monster-015.png", Game.Client.Services.MonsterArt.ForName(monsters[3].Name));
        var active = monsters[1];
        Assert.Equal((140, 252, 15, 5, "saved-profile", "kobold-mine-entry-lv3"),
            (active.BaseMaxHp, active.MaxHp, active.Attack, active.Defense, active.CombatProfileCode, active.RewardProfileCode));
        Assert.Equal("kobold-mine-entry-boss", monsters[3].RewardProfileCode);
        var saved = await db.Rooms.FindAsync(room.Id);
        Assert.Equal((202, RoomStatus.Preparing, 2, 4, 7, 3, 2, 10, true),
            (saved!.MonsterId, saved.Status, saved.CurrentWaveNumber, saved.TotalWaveCount,
                saved.RoundNumber, saved.RunSequence, saved.ScalingPartySize, saved.Version, saved.IsRepeatBattle));
        Assert.Equal(1, (await db.Rooms.FindAsync(oldRoom.Id))!.Version);
        Assert.Equal(0, (await db.Rooms.FindAsync(hiddenRoom.Id))!.Version);
        Assert.Equal(intent.Id, (await db.MonsterIntents.SingleAsync()).Id);
        Assert.Equal("saved-skill", (await db.MonsterIntents.SingleAsync()).SkillCode);
        Assert.Equal(dungeon.Id, (await db.UserDungeonClears.SingleAsync()).DungeonId);
        Assert.Equal("Pending", (await db.RewardRuns.SingleAsync()).Status);
        Assert.Equal("monster:1:1", (await db.RewardEvents.SingleAsync()).EventKey);
        Assert.Equal(12, (await db.RewardEntries.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task StartupRefreshesSharedLootProfilesAndPreservesPendingWeaponRewards()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        var content = new Content();
        await DbInitializer.InitializeAsync(db, world: content.World, encounters: content.Encounters);
        var dungeon = await db.Dungeons.SingleAsync(item => item.Code == "kobold-mine");
        var room = new Room { Id = 101, DungeonId = dungeon.Id, MonsterId = 202,
            CurrentWaveNumber = 2, RoundNumber = 4, RunSequence = 2, Version = 7 };
        db.Rooms.Add(room);
        db.Monsters.Add(new Monster { Id = 202, RoomId = room.Id, Name = "烛矿掘道者", Element = ElementType.Earth,
            WaveNumber = 2, Position = 1, Hp = 41, MaxHp = 140, BaseMaxHp = 140, Attack = 15, Defense = 5,
            RewardProfileCode = "kobold-mine-worker" });
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new WeaponRewardSnapshot("t1-fang-hunting-spear",
            "兽牙猎矛", ElementType.Wind, 24, 40, 1, 10, 1,
            [new WeaponRewardSkillSnapshot("weapon-attack", 2)], QualityRank: 3));
        db.RewardEntries.Add(new RewardEntry { RoomId = room.Id, Sequence = 2, EventKey = "monster:1:1",
            UserId = 1, CharacterId = 1, Kind = "Weapon", Code = "t1-fang-hunting-spear", Quantity = 1,
            WeaponSnapshotJson = snapshot });
        await db.SaveChangesAsync();

        await DbInitializer.InitializeAsync(db, world: content.World, encounters: content.Encounters);
        await DbInitializer.InitializeAsync(db, world: content.World, encounters: content.Encounters);
        db.ChangeTracker.Clear();
        var monster = await db.Monsters.SingleAsync();
        Assert.Equal("kobold-mine-entry-lv3", monster.RewardProfileCode);
        Assert.Equal((41, 140, 140, 15, 5), (monster.Hp, monster.MaxHp, monster.BaseMaxHp, monster.Attack, monster.Defense));
        var savedRoom = await db.Rooms.SingleAsync();
        Assert.Equal((2, 4, 2, 8), (savedRoom.CurrentWaveNumber, savedRoom.RoundNumber, savedRoom.RunSequence, savedRoom.Version));
        var reward = await db.RewardEntries.SingleAsync();
        Assert.Equal(("t1-fang-hunting-spear", 1, snapshot), (reward.Code, reward.Quantity, reward.WeaponSnapshotJson));
    }

    private sealed class Content
    {
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        public IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(_configuration.GetSection(section).Get<T>()!);
        public WorldCatalog World { get; } = WorldCatalog.LoadDefault();
        public ConsumableCatalog Consumables { get; }
        public WeaponCatalog Weapons { get; }
        public MaterialCatalog Materials { get; }
        public SoulImprintCatalog SoulImprints { get; }
        public RewardOptions RewardOptions { get; }
        public RewardCatalog Rewards { get; }
        public MonsterCombatCatalog Combat { get; }
        public DungeonEncounterCatalog Encounters { get; }
        public DungeonExchangeCatalog Exchanges { get; }

        public Content()
        {
            Consumables = new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
            Weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Materials = new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName));
            SoulImprints = new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
            RewardOptions = Bind<RewardOptions>(Configuration.RewardOptions.SectionName).Value;
            Rewards = new RewardCatalog(Options.Create(RewardOptions), Consumables, Weapons, Materials, SoulImprints);
            Combat = new MonsterCombatCatalog(Bind<MonsterCombatOptions>(MonsterCombatOptions.SectionName));
            Encounters = new DungeonEncounterCatalog(Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName), Combat, Rewards);
            Exchanges = new DungeonExchangeCatalog(Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName),
                Materials, Weapons, SoulImprints);
        }
    }
}
