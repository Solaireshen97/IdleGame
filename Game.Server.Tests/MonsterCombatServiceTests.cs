using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public class MonsterCombatServiceTests
{
    [Fact]
    public async Task PlannedIntentIsPersistedAndDoesNotReroll()
    {
        await using var test = await Context.CreateAsync("acid-slime");

        var first = await test.Service.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        var room = await test.Db.Rooms.SingleAsync();
        var monster = await test.Db.Monsters.SingleAsync();
        var second = await test.Service.EnsureIntentAsync(room, monster);
        var response = await test.Service.GetIntentResponseAsync(room, monster);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("acid", second.SkillCode);
        Assert.Equal(test.Character.Id, second.TargetCharacterId);
        Assert.Equal("腐蚀喷射", response!.ActionName);
        Assert.Contains("Knight", response.TargetLabel);
    }

    [Fact]
    public async Task PlannedFrontIntentKeepsItsActionButTracksFormationChanges()
    {
        await using var test = await Context.CreateAsync("acid-slime");
        var first = await test.Service.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();
        var replacement = new Character
        {
            UserId = 1, Name = "Cleric", Hp = 100, MaxHp = 100, Attack = 15
        };
        test.Db.Characters.Add(replacement);
        await test.Db.SaveChangesAsync();
        test.Slot.CharacterId = replacement.Id;
        await test.Db.SaveChangesAsync();

        var updated = await test.Service.EnsureIntentAsync(test.Room, test.Monster);

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("acid", updated.SkillCode);
        Assert.Equal(replacement.Id, updated.TargetCharacterId);
    }

    [Fact]
    public async Task SkillExecutionDealsDamageAppliesStatusAndStartsCooldown()
    {
        await using var test = await Context.CreateAsync("acid-slime");
        var intent = await test.Service.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();

        await test.Service.ExecuteIntentAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], new Dictionary<int, ElementType>(), default, []);
        await test.Db.SaveChangesAsync();

        Assert.Equal(88, test.Character.Hp);
        var effect = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal("armor-break", effect.EffectCode);
        Assert.Equal(2, effect.ExpiresAfterRound);
        Assert.Equal(3, (await test.Db.BattleMonsterSkillCooldowns.SingleAsync()).ReadyAtRound);
        Assert.False(await test.Db.MonsterIntents.AnyAsync(entry => entry.Id == intent.Id));
    }

    [Fact]
    public async Task PoisonStartsNextRoundAndExpiresAfterConfiguredDuration()
    {
        await using var test = await Context.CreateAsync("toxic-slime");
        await test.Service.ExecuteIntentAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], new Dictionary<int, ElementType>(), default, []);
        await test.Db.SaveChangesAsync();
        Assert.Equal(92, test.Character.Hp);

        test.Room.RoundNumber = 1;
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], []);
        Assert.Equal(88, test.Character.Hp);
        Assert.Single(await test.Service.GetStatusResponsesAsync(test.Room, "Character", test.Character.Id));

        test.Room.RoundNumber = 2;
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], []);
        await test.Db.SaveChangesAsync();
        Assert.Equal(84, test.Character.Hp);
        Assert.Empty(await test.Service.GetStatusResponsesAsync(test.Room, "Character", test.Character.Id));
    }

    [Fact]
    public async Task DeadlyIntentCanBeConfiguredAsUninterruptible()
    {
        await using var test = await Context.CreateAsync("toxic-slime");

        var response = await test.Service.GetIntentResponseAsync(test.Room, test.Monster);
        var interrupted = await test.Service.InterruptCurrentIntentAsync(test.Room, test.Monster);

        Assert.NotNull(response);
        Assert.Equal("Deadly", response.DangerLevel);
        Assert.False(response.IsInterruptible);
        Assert.False(interrupted);
    }

    [Fact]
    public async Task SilenceCancelsOnlyTheFollowingRoundsInterruptibleSkill()
    {
        await using var test = await Context.CreateAsync("rapid-slime");
        Assert.True(await test.Service.InterruptCurrentIntentAsync(test.Room, test.Monster));
        await test.Service.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "acolyte-silence", 1, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();
        await test.Service.ExecuteIntentAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], new Dictionary<int, ElementType>(), default, []);
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], []);
        await test.Db.SaveChangesAsync();

        test.Room.RoundNumber = 1;
        var next = await test.Service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal("rapid", next.SkillCode);
        Assert.True(next.IsInterrupted);
        var logs = new List<string>();
        await test.Service.ExecuteIntentAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], new Dictionary<int, ElementType>(), default, logs);
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], logs);
        await test.Db.SaveChangesAsync();
        Assert.Equal(100, test.Character.Hp);
        Assert.Contains(logs, log => log.Contains("沉默影响"));

        test.Room.RoundNumber = 2;
        Assert.False((await test.Service.EnsureIntentAsync(test.Room, test.Monster)).IsInterrupted);
    }

    [Fact]
    public async Task SilenceDoesNotCancelUninterruptibleSkillsOrBasicAttacks()
    {
        await using var test = await Context.CreateAsync("toxic-slime");
        await test.Service.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "acolyte-silence", 1, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();
        test.Room.RoundNumber = 1;
        var intent = await test.Service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal("toxic", intent.SkillCode);
        Assert.False(intent.IsInterrupted);

        await using var basic = await Context.CreateAsync("basic-attacks-only");
        await basic.Service.ApplyStatusAsync(basic.Room, "Monster", basic.Monster.Id,
            "acolyte-silence", 1, [], basic.Monster.Name);
        await basic.Db.SaveChangesAsync();
        basic.Room.RoundNumber = 1;
        var basicIntent = await basic.Service.EnsureIntentAsync(basic.Room, basic.Monster);
        Assert.Equal("BasicAttack", basicIntent.ActionType);
        Assert.False(basicIntent.IsInterrupted);
    }

    [Fact]
    public async Task DispelledStatusCanBeReappliedLaterInTheSameRound()
    {
        await using var test = await Context.CreateAsync("hardened-slime");
        await test.Service.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "slime-shell", 1, [], "Slime");
        await test.Db.SaveChangesAsync();
        var removed = await test.Service.RemoveFirstStatusAsync(test.Room, "Monster", [test.Monster.Id], true);

        await test.Service.ExecuteIntentAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], new Dictionary<int, ElementType>(), default, []);
        await test.Db.SaveChangesAsync();

        Assert.NotNull(removed);
        var effect = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal("slime-shell", effect.EffectCode);
        Assert.Equal(2, effect.ExpiresAfterRound);
    }

    [Fact]
    public async Task SwordGuardStanceProvidesPassiveTenPercentReduction()
    {
        await using var test = await Context.CreateAsync("basic-attacks-only");
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
            { CharacterId = test.Character.Id, NodeCode = "sword-guard-stance", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();

        await test.Service.ExecuteIntentAsync(test.Room, test.Monster,
            [new(test.Slot, test.Character)], new Dictionary<int, ElementType>(), default, []);

        Assert.Equal(91, test.Character.Hp);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly string _path;
        private Context(string path, GameDbContext db, Room room, Monster monster, Character character, RoomSlot slot)
        {
            _path = path;
            Db = db;
            Room = room;
            Monster = monster;
            Character = character;
            Slot = slot;
            Service = new MonsterCombatService(db, MonsterCombatTestFactory.CreateCatalog());
        }

        public GameDbContext Db { get; }
        public Room Room { get; }
        public Monster Monster { get; }
        public Character Character { get; }
        public RoomSlot Slot { get; }
        public MonsterCombatService Service { get; }

        public static async Task<Context> CreateAsync(string profileCode)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-monster-combat-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5 };
            var monster = new Monster
            {
                Id = 1, RoomId = 1, Name = "Slime", Element = ElementType.Wind, Hp = 50, MaxHp = 50,
                Attack = 10, Defense = 2, CombatProfileCode = profileCode
            };
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20};
            var slot = new RoomSlot { Id = 1, RoomId = 1, SlotIndex = 1, CharacterId = 1, UserId = 1, IsMainControl = true };
            db.AddRange(room, monster, character, slot);
            await db.SaveChangesAsync();
            return new Context(path, db, room, monster, character, slot);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
