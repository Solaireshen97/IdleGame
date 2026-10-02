using Game.Server.Services;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProductionBattleGraphRestoresContextInFreshScopes(bool erosion, bool detailFirst)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var catalog = LightProductionCatalog();
        if (erosion) test.Monster.CombatProfileCode = "dark-deep-lv3-boss";
        test.Character.Hp = 70;
        await test.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddDbContext<GameDbContext>(options => options.UseSqlite(test.Db.Database.GetConnectionString()));
        services.AddSingleton(catalog);
        services.AddSingleton(catalog.Statuses);
        services.AddSingleton(RewardTestFactory.CreateCatalog());
        services.AddSingleton(PartyScalingCatalog.Default);
        services.AddSingleton(new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions())));
        services.AddSingleton(ProgressionTestFactory.Create());
        services.AddSingleton(SkillTestFactory.Create());
        services.AddSingleton(ConsumableTestFactory.Create());
        services.AddScoped<UserService>();
        services.AddBattleServices();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true, ValidateScopes = true
        });
        await using (var seed = provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<GameDbContext>();
            var room = await db.Rooms.SingleAsync();
            await seed.ServiceProvider.GetRequiredService<DungeonRunRulesService>().EnsureAsync(room);
            if (erosion)
                await seed.ServiceProvider.GetRequiredService<BattleStatusService>().ApplyAsync(room,
                    "Character", test.Character.Id, "dark-deep-lv3-erosion", 1, [], "Knight",
                    boundTargetType: "Monster", boundTargetId: test.Monster.Id, magnitudeSnapshot: 100);
            await db.SaveChangesAsync();
        }

        // Each request starts with an empty rule cache, just like the background room worker.
        for (var request = 0; request < 2; request++)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var actor = await db.Characters.SingleAsync();
            Assert.Null(actor.BattleMaxHpLimit);
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var battles = scope.ServiceProvider.GetRequiredService<BattleService>();
            var combat = scope.ServiceProvider.GetRequiredService<MonsterCombatService>();
            Assert.Same(scope.ServiceProvider.GetRequiredService<MonsterPhaseService>(), combat.Phases);
            Assert.Same(scope.ServiceProvider.GetRequiredService<BattleStatusService>(), combat.Statuses);
            if (detailFirst) Assert.NotNull(await rooms.GetRoomDetailAsync(test.Room.Id, test.Token));
            var result = await battles.SyncRoomAsync(test.Room.Id);
            Assert.Null(result.Error);
            Assert.NotNull(result.Result);
            if (!detailFirst) Assert.NotNull(await rooms.GetRoomDetailAsync(test.Room.Id, test.Token));
            Assert.Equal(erosion ? 90 : 100, TalentRules.EffectiveMaxHp(actor));
            Assert.Equal(70, actor.Hp);
            Assert.Equal(100, actor.MaxHp);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshBattleRequestLoadsFrozenRulesBeforeHealthProjection(bool preloadRules)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var catalog = LightProductionCatalog();
        var rewards = RewardTestFactory.CreateCatalog();
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var initialRules = new DungeonRunRulesService(test.Db, catalog, rewards, PartyScalingCatalog.Default, depths);
        await initialRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var rules = new DungeonRunRulesService(db, catalog, rewards, PartyScalingCatalog.Default, depths);
        if (preloadRules) await rules.EnsureAsync(await db.Rooms.SingleAsync());
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var combat = new MonsterCombatService(db, catalog, runRules: rules);
        var user = new UserService(db, progression, skills);
        var rewardService = new RewardService(db, rewards, progression, runRules: rules);
        var consumables = ConsumableTestFactory.Create();
        var rooms = new RoomService(db, user, progression, consumables, skills, rewardService,
            monsterCombatService: combat, runRules: rules);
        var battle = new BattleService(db, user, consumables, skills, rewardService,
            monsterCombatService: combat, roomService: rooms, runRules: rules);
        var result = await battle.SyncRoomAsync(test.Room.Id);
        Assert.Null(result.Error);
        Assert.NotNull(result.Result);
    }

    [Fact]
    public async Task RandomPreviewExcludesDeathAlreadyResolvedInCurrentTransaction()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 10);
        var rear = await test.AddSlotAsync(2, "Rear");
        test.Monster.CombatProfileCode = "light-deep-lv1-monster-1";
        var service = new MonsterCombatService(test.Db, LightProductionCatalog(), new LightLastCandidateRandom());
        await service.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();

        // Production creates the next intent before SaveResultAsync commits this round's HP changes.
        rear.Hp = 0;
        test.Room.RoundNumber = 1;
        var intent = await service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal("light-deep-lv1-wraith-bolt", intent.SkillCode);
        Assert.Equal(test.Character.Id, intent.TargetCharacterId);
    }

    [Fact]
    public async Task RandomPreviewIncludesRevivalAlreadyResolvedInCurrentTransaction()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rear = await test.AddSlotAsync(2, "Rear", hp: 0);
        test.Monster.CombatProfileCode = "light-deep-lv1-monster-1";
        var service = new MonsterCombatService(test.Db, LightProductionCatalog(), new LightLastCandidateRandom());
        await service.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();
        rear.Hp = 50;
        test.Character.Hp = 0;
        test.Room.RoundNumber = 1;
        Assert.Equal(rear.Id, (await service.EnsureIntentAsync(test.Room, test.Monster)).TargetCharacterId);
    }

    [Fact]
    public async Task FrontPreviewUsesCurrentFormationBeforeTheRoundIsSaved()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rear = await test.AddSlotAsync(2, "Rear");
        var service = new MonsterCombatService(test.Db, LightProductionCatalog());
        var intent = await service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal(test.Character.Id, intent.TargetCharacterId);
        await test.Db.SaveChangesAsync();
        var frontSlot = await test.Db.RoomSlots.SingleAsync(s => s.SlotIndex == 1);
        var rearSlot = await test.Db.RoomSlots.SingleAsync(s => s.SlotIndex == 2);
        frontSlot.CharacterId = rear.Id;
        rearSlot.CharacterId = test.Character.Id;
        Assert.Equal(rear.Id, (await service.EnsureIntentAsync(test.Room, test.Monster)).TargetCharacterId);
        rear.Hp = 0;
        Assert.Equal(test.Character.Id, (await service.EnsureIntentAsync(test.Room, test.Monster)).TargetCharacterId);
    }

    [Theory]
    [InlineData("wind-deep-lv2-imbalance")]
    [InlineData("light-deep-lv2-imbalance")]
    public async Task DirectOnlyBreakRewardDoesNotAmplifyExistingDot(string reward)
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var statuses = new BattleStatusService(test.Db, LightProductionCatalog().Statuses);
        await statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "rogue-poison", 3, [], "Boss", perTickValue: 100);
        test.Room.RoundNumber = 1;
        await statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, reward, 2, [], "Boss");
        Assert.Equal(120, await statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
        await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster,
            [new(await test.Db.RoomSlots.SingleAsync(), test.Character)], []);
        Assert.Equal(900, test.Monster.Hp);
    }

    [Theory]
    [InlineData(BattleDamageScope.All, 130, 870)]
    [InlineData(BattleDamageScope.Direct, 130, 900)]
    [InlineData(BattleDamageScope.Periodic, 100, 870)]
    public async Task FrozenDamageScopeControlsDirectAndPeriodicDamage(BattleDamageScope scope,
        int expectedDirect, int expectedHp)
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var options = LightProductionCatalog().ExportOptions();
        var reward = options.StatusEffects.Single(s => s.Code == "wind-deep-lv2-imbalance");
        reward.DamageScope = scope;
        var catalog = new MonsterCombatCatalog(Options.Create(options));
        var rewards = RewardTestFactory.CreateCatalog();
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rules = new DungeonRunRulesService(test.Db, catalog, rewards, PartyScalingCatalog.Default, depths);
        await rules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();

        // A changed live catalog must not replace a previously captured damage scope.
        reward.DamageScope = scope == BattleDamageScope.All ? BattleDamageScope.Direct : BattleDamageScope.All;
        catalog = new MonsterCombatCatalog(Options.Create(options));
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        var loaded = new DungeonRunRulesService(db, catalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(db, catalog.Statuses, runRules: loaded);
        await statuses.ApplyAsync(room, "Monster", monster.Id, "rogue-poison", 3, [], "Boss", perTickValue: 100);
        room.RoundNumber = 1;
        await statuses.ApplyAsync(room, "Monster", monster.Id, reward.Code, 2, [], "Boss", magnitudeSnapshot: 30);
        Assert.Equal(expectedDirect, await statuses.AmplifyDamageAsync(room, monster.Id, 100));
        await statuses.ResolveEndOfRoundAsync(room, monster, await BattlePartyReader.ReadAsync(db, room.Id), []);
        Assert.Equal(expectedHp, monster.Hp);
    }

    [Theory]
    [InlineData((BattleDamageScope)0, "DamageTakenPercent")]
    [InlineData((BattleDamageScope)4, "DamageTakenPercent")]
    [InlineData(BattleDamageScope.Direct, "AttackPercent")]
    public void DamageScopeRejectsUnsupportedConfiguration(BattleDamageScope scope, string effectType)
    {
        var options = new MonsterCombatOptions
        {
            StatusEffects = [new() { Code = "test", Name = "Test", Description = "Test",
                EffectType = effectType, ValuePerStack = 20, DamageScope = scope }]
        };
        Assert.Throws<InvalidOperationException>(() => new BattleStatusCatalog(Options.Create(options)));
    }

    [Fact]
    public void MissingDamageScopePreservesGeneralVulnerability()
    {
        var definition = System.Text.Json.JsonSerializer.Deserialize<BattleStatusOptions>(
            """{"Code":"legacy","Name":"Legacy","Description":"Legacy","EffectType":"DamageTakenPercent","ValuePerStack":20}""")!;
        var catalog = new BattleStatusCatalog(Options.Create(new MonsterCombatOptions { StatusEffects = [definition] }));
        Assert.Equal(BattleDamageScope.All, catalog.Find("legacy")!.DamageScope);
    }
}
