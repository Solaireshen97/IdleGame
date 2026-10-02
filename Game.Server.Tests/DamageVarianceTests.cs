using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DamageVarianceTests
{
    [Theory]
    [InlineData(0, 97)]
    [InlineData(0.999999999999, 103)]
    public void ThreePercentDamageHasExpectedLowerAndUpperBounds(double roll, int expected)
    {
        Assert.Equal(expected, DamageVariance.Roll(100, 3, new SequenceRandom(roll, 0)));
    }

    [Fact]
    public void SmallDamageSometimesMovesByOneWithoutSystematicDownwardRounding()
    {
        // At either endpoint, ten equally spaced rounding samples give exact expected frequencies.
        var results = new List<int>();
        for (var index = 0; index < 10; index++)
        {
            var roundingRoll = (index + 0.5) / 10;
            results.Add(DamageVariance.Roll(10, 3, new SequenceRandom(0, roundingRoll)));
            results.Add(DamageVariance.Roll(10, 3, new SequenceRandom(1, roundingRoll)));
        }
        Assert.Equal(3, results.Count(value => value == 9));
        Assert.Equal(14, results.Count(value => value == 10));
        Assert.Equal(3, results.Count(value => value == 11));
        Assert.Equal(10m, results.Sum() / (decimal)results.Count);
    }

    [Fact]
    public void DisabledVarianceAndNonpositiveDamageDoNotConsumeRandom()
    {
        var random = new SequenceRandom(); // Any attempted draw fails immediately.
        Assert.Equal(100, DamageVariance.Roll(100, 0, random));
        Assert.Equal(0, DamageVariance.Roll(0, 3, random));
        Assert.Equal(-1, DamageVariance.Roll(-1, 3, random));
        Assert.Equal(0, random.Calls);
        Assert.Equal(0m, new CombatDamageOptions().VariancePercent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidVarianceIsRejectedBeforeRandomDraw(int percent)
    {
        var random = new SequenceRandom();
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageVariance.Roll(100, percent, random));
        Assert.Equal(0, random.Calls);
    }

    [Fact]
    public void PositiveDamageClampsBeforeRoundingAndIntegerResultsNeedOnlyOneDraw()
    {
        var lower = new SequenceRandom(0);
        Assert.Equal(1, DamageVariance.Roll(1, 100, lower));
        Assert.Equal(1, lower.Calls);
        var upper = new SequenceRandom(1);
        Assert.Equal(int.MaxValue, DamageVariance.Roll(int.MaxValue, 100, upper));
        Assert.Equal(1, upper.Calls);
        var integer = new SequenceRandom(0.5);
        Assert.Equal(10, DamageVariance.Roll(10, 3, integer));
        Assert.Equal(1, integer.Calls);
    }

    [Fact]
    public async Task CharacterDamageRecordsRolledAmountAndActualOverkillConsistently()
    {
        await using var test = await Scenario.CreateAsync();
        test.Monster.Hp = 50;
        var random = new SequenceRandom(0);
        var damage = test.NewDamage(random);
        var battle = test.Context();
        using var recording = test.Events.Begin(test.Room, test.Monster, battle.Party);
        var result = await damage.CharacterDamageAsync(battle, battle.Characters[0], BattleSkillEffect.Damage(100),
            BattleDamageOrigin.NormalAttack, false);
        Assert.Equal((97, 50, false), (result.CalculatedAmount, result.ActualAmount, result.IsCritical));
        Assert.Equal(0, test.Monster.Hp);
        var hit = Assert.Single(test.Events.Snapshot(test.Room), fact => fact.Kind == BattleEventKind.Damage);
        Assert.Equal((97, 50, (int?)50, (int?)0), (hit.CalculatedAmount, hit.ActualAmount, hit.HpBefore, hit.HpAfter));
        Assert.Single(test.Events.Snapshot(test.Room), fact => fact.Kind == BattleEventKind.Defeat);
        Assert.Equal(1, random.Calls);
    }

    [Fact]
    public async Task MonsterDamageVariesAfterGuardAndRecordsTheSameHpChange()
    {
        await using var test = await Scenario.CreateAsync();
        test.Monster.Attack = 200;
        await test.Guards.ApplyAsync(test.Room, test.Character.Id, 50, null, false);
        var random = new SequenceRandom(0);
        var battle = test.Context();
        using var recording = test.Events.Begin(test.Room, test.Monster, battle.Party);
        var result = await test.NewDamage(random).MonsterDamageAsync(battle, battle.Characters[0],
            BattleSkillEffect.Damage(100), areaAttack: false);
        Assert.Equal((97, 97), (result.CalculatedAmount, result.ActualAmount));
        Assert.Equal(1903, test.Character.Hp);
        var hit = Assert.Single(test.Events.Snapshot(test.Room), fact => fact.Kind == BattleEventKind.Damage);
        Assert.Equal((97, 97, (int?)2000, (int?)1903), (hit.CalculatedAmount, hit.ActualAmount, hit.HpBefore, hit.HpAfter));
        Assert.Equal("Monster", hit.Source!.ActorType);
        Assert.Equal("Character", hit.Target.ActorType);
        Assert.Equal(1, random.Calls);
    }

    [Fact]
    public async Task VarianceFollowsDefenseCriticalGuardAndFinalMultipliers()
    {
        await using var test = await Scenario.CreateAsync();
        test.Character.WeaponCriticalChancePercent = 100;
        test.Monster.Defense = 20;
        await test.Guards.ApplyForActorAsync(test.Room, "Monster", test.Monster.Id, 25, null);
        var random = new SequenceRandom(0, 0.99);
        var battle = test.Context();
        using var recording = test.Events.Begin(test.Room, test.Monster, battle.Party);
        var result = await test.NewDamage(random).CharacterDamageAsync(battle, battle.Characters[0],
            BattleSkillEffect.Damage(100), BattleDamageOrigin.Skill, true, multipliers: [1.5m]);
        // (100 - 20) * 1.5 critical * .75 guard = 90; final multiplier makes 135;
        // -3% makes 130.95, whose .99 rounding sample yields 130.
        Assert.Equal(130, result.CalculatedAmount);
        Assert.True(result.IsCritical);
        Assert.Equal(1870, test.Monster.Hp);
        Assert.True(Assert.Single(test.Events.Snapshot(test.Room), fact => fact.Kind == BattleEventKind.Damage).IsCritical);
        Assert.Equal(2, random.Calls); // Guaranteed critical chance does not draw separately.
    }

    [Fact]
    public async Task SeparateDirectHitsConsumeIndependentVarianceDraws()
    {
        await using var test = await Scenario.CreateAsync();
        var random = new SequenceRandom(0, 1);
        var damage = test.NewDamage(random);
        var battle = test.Context();
        using var recording = test.Events.Begin(test.Room, test.Monster, battle.Party);
        var first = await damage.CharacterDamageAsync(battle, battle.Characters[0], BattleSkillEffect.Damage(100),
            BattleDamageOrigin.Skill, false);
        var second = await damage.CharacterDamageAsync(battle, battle.Characters[0], BattleSkillEffect.Damage(100),
            BattleDamageOrigin.Skill, false);
        Assert.Equal((97, 103), (first.CalculatedAmount, second.CalculatedAmount));
        Assert.Equal(1800, test.Monster.Hp);
        Assert.Equal(new[] { 97, 103 }, test.Events.Snapshot(test.Room).Select(fact => fact.CalculatedAmount));
        Assert.Equal(2, random.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenRoomAndLegacyJsonKeepTheirVarianceWhileNewRoomsUseThreePercent(bool legacyJson)
    {
        await using var test = await Scenario.CreateAsync();
        var originalRules = test.NewRules(legacyJson ? 3 : 1);
        await originalRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        if (legacyJson)
        {
            var snapshot = await test.Db.DungeonRunRuleSnapshots.SingleAsync();
            var json = JsonNode.Parse(snapshot.DefinitionJson)!;
            Assert.True(json.AsObject().Remove("DirectDamageVariancePercent"));
            snapshot.DefinitionJson = json.ToJsonString();
            snapshot.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.DefinitionJson))).ToLowerInvariant();
            await test.Db.SaveChangesAsync();
        }
        test.Db.ChangeTracker.Clear();
        var room = await test.Db.Rooms.SingleAsync();
        var monster = await test.Db.Monsters.SingleAsync();
        var character = await test.Db.Characters.SingleAsync();
        var newRules = test.NewRules(3);
        Assert.Equal(legacyJson ? 0m : 1m, (await newRules.EnsureAsync(room)).DirectDamageVariancePercent);
        var battle = Scenario.Context(room, monster, character);
        var random = legacyJson ? new SequenceRandom() : new SequenceRandom(0);
        var service = test.NewDamage(random, newRules, configuredPercent: 3);
        var result = await service.CharacterDamageAsync(battle, battle.Characters[0], BattleSkillEffect.Damage(100),
            BattleDamageOrigin.NormalAttack, false);
        Assert.Equal(legacyJson ? 100 : 99, result.CalculatedAmount);
        Assert.Equal(legacyJson ? 0 : 1, random.Calls);

        var freshRoom = new Room { DungeonId = room.DungeonId, MonsterId = monster.Id, SlotCount = 5 };
        test.Db.Rooms.Add(freshRoom);
        await test.Db.SaveChangesAsync();
        Assert.Equal(3m, (await newRules.EnsureAsync(freshRoom)).DirectDamageVariancePercent);
        var freshBattle = Scenario.Context(freshRoom, monster, character);
        var freshResult = await test.NewDamage(new SequenceRandom(0), newRules, configuredPercent: 0)
            .CharacterDamageAsync(freshBattle, freshBattle.Characters[0], BattleSkillEffect.Damage(100),
                BattleDamageOrigin.NormalAttack, false);
        Assert.Equal(97, freshResult.CalculatedAmount); // Frozen policy wins over caller configuration.
    }

    [Fact]
    public async Task HealingAndPeriodicDamageStayExactWithDirectVarianceEnabled()
    {
        await using var test = await Scenario.CreateAsync();
        var random = new SequenceRandom();
        _ = test.NewDamage(random);
        test.Character.Hp = 1000;
        var battle = test.Context();
        Assert.Equal(100, BattleDamageService.Heal(battle.Characters[0], battle.Characters[0],
            new BattleSkillEffect(BattleEffectKind.Heal, default, Power: 100, AttackPowerPercent: 0)));
        Assert.Equal(1100, test.Character.Hp);
        await test.Statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "poison", 1, [], "Enemy", 100,
            new("Character", test.Character.Id, "poison-skill"));
        await test.Db.SaveChangesAsync();
        test.Room.RoundNumber = 1;
        using var recording = test.Events.Begin(test.Room, test.Monster, battle.Party);
        await test.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, battle.Party, []);
        Assert.Equal(1900, test.Monster.Hp);
        var tick = Assert.Single(test.Events.Snapshot(test.Room), fact => fact.Kind == BattleEventKind.Damage);
        Assert.Equal((100, 100), (tick.CalculatedAmount, tick.ActualAmount));
        Assert.Equal(BattleActionKind.Periodic, tick.ActionKind);
        Assert.Equal(0, random.Calls);
    }

    private sealed class SequenceRandom(params double[] values) : Random
    {
        public int Calls { get; private set; }
        public override double NextDouble() => values[Calls++];
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public GameDbContext Db { get; }
        public Room Room { get; }
        public Monster Monster { get; }
        public Character Character { get; }
        public BattleEventCollector Events { get; } = new();
        public BattleStatusService Statuses { get; }
        public BattleGuardService Guards { get; }

        private Scenario(SqliteConnection connection, GameDbContext db, Room room, Monster monster, Character character)
        {
            (_connection, Db, Room, Monster, Character) = (connection, db, room, monster, character);
            Statuses = new BattleStatusService(db, MonsterCombatTestFactory.CreateCatalog().Statuses, Events);
            Guards = new BattleGuardService(Statuses);
        }

        public BattleDamageService NewDamage(Random random, DungeonRunRulesService? rules = null, decimal configuredPercent = 3) =>
            new(Statuses, Guards, random, Events, rules, Options.Create(new CombatDamageOptions { VariancePercent = configuredPercent }));

        public DungeonRunRulesService NewRules(decimal percent) => new(Db, MonsterCombatTestFactory.CreateCatalog(),
            RewardTestFactory.CreateCatalog(), PartyScalingCatalog.Default,
            new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions())),
            damageOptions: Options.Create(new CombatDamageOptions { VariancePercent = percent }));

        public BattleExecutionContext Context() => Context(Room, Monster, Character);
        public static BattleExecutionContext Context(Room room, Monster monster, Character character) => new(room, monster,
            [new(new RoomSlot { RoomId = room.Id, CharacterId = character.Id, UserId = character.UserId, SlotIndex = 1 }, character)],
            new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);

        public static async Task<Scenario> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, SlotCount = 5 };
            var monster = new Monster { Id = 1, RoomId = 1, WaveNumber = 1, Position = 1, Name = "Monster",
                Attack = 100, Hp = 2000, MaxHp = 2000, BaseMaxHp = 2000, RewardProfileCode = "slime-field" };
            var character = new Character { Id = 1, UserId = 1, Name = "Player", Attack = 100, Hp = 2000, MaxHp = 2000 };
            db.AddRange(new Dungeon { Id = 1, Code = "slime-field", Name = "Test", DungeonKind = "Dungeon" },
                new User { Id = 1, UserName = "test", PasswordHash = "test" }, character, room, monster);
            await db.SaveChangesAsync();
            return new Scenario(connection, db, room, monster, character);
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
