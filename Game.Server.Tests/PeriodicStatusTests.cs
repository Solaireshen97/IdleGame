using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class PeriodicStatusTests
{
    [Fact]
    public async Task HealingAuraPersistsThreeTicksAcrossWavesAndKeepsStrongerPotency()
    {
        await using var test = await Fixture.CreateAsync();
        test.Character.Hp = 72;
        await test.Service.ApplyStatusAsync(test.Room, "Character", test.Character.Id,
            "renew", 3, [], test.Character.Name, perTickValue: 10);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        var effect = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal(10, effect.PerTickValue);

        await test.Service.ApplyStatusAsync(test.Room, "Character", test.Character.Id,
            "renew", 3, [], test.Character.Name, perTickValue: 4);
        test.Room.RoundNumber = 1;
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster, test.Participants, []);
        Assert.Equal(82, test.Character.Hp);
        Assert.Equal(10, (await test.Db.BattleStatusEffects.SingleAsync()).PerTickValue);

        test.Room.RoundNumber = 2;
        var nextWave = new Monster { Id = 2, RoomId = test.Room.Id, Name = "Second", Hp = 50, MaxHp = 50 };
        await test.Service.ResolveEndOfRoundAsync(test.Room, nextWave, test.Participants, []);
        Assert.Equal(92, test.Character.Hp);

        test.Room.RoundNumber = 3;
        await test.Service.ResolveEndOfRoundAsync(test.Room, nextWave, test.Participants, []);
        Assert.Equal(100, test.Character.Hp);

        test.Room.RoundNumber = 4;
        await test.Service.ResolveEndOfRoundAsync(test.Room, nextWave, test.Participants, []);
        Assert.Equal(100, test.Character.Hp);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
    }

    [Fact]
    public async Task DamageAuraUsesSnapshottedTickAndDoesNotDelayOnRefresh()
    {
        await using var test = await Fixture.CreateAsync();
        await test.Service.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "burn", 3, [], test.Monster.Name, perTickValue: 7);
        await test.Db.SaveChangesAsync();

        test.Room.RoundNumber = 1;
        await test.Service.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "burn", 1, [], test.Monster.Name, perTickValue: 3);
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster, test.Participants, []);
        Assert.Equal(43, test.Monster.Hp);
        var effect = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal(7, effect.PerTickValue);
        Assert.Equal(3, effect.ExpiresAfterRound);

        test.Room.RoundNumber = 2;
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster, test.Participants, []);
        test.Room.RoundNumber = 3;
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster, test.Participants, []);
        Assert.Equal(29, test.Monster.Hp);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
    }

    [Fact]
    public async Task HealingAuraDoesNotReviveDeadCharacter()
    {
        await using var test = await Fixture.CreateAsync();
        await test.Service.ApplyStatusAsync(test.Room, "Character", test.Character.Id,
            "renew", 3, [], test.Character.Name, perTickValue: 10);
        await test.Db.SaveChangesAsync();
        test.Character.Hp = 0;
        test.Room.RoundNumber = 1;
        await test.Service.ResolveEndOfRoundAsync(test.Room, test.Monster, test.Participants, []);
        Assert.Equal(0, test.Character.Hp);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path;
        private Fixture(string path, GameDbContext db, Room room, Monster monster, Character character, RoomSlot slot)
        {
            _path = path;
            Db = db;
            Room = room;
            Monster = monster;
            Character = character;
            Participants = [new(slot, character)];
            Service = new MonsterCombatService(db, new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions
            {
                StatusEffects =
                [
                    new() { Code = "burn", Name = "Burn", Description = "Burns", EffectType = "DamageOverTime", ValuePerStack = 0 },
                    new() { Code = "renew", Name = "Renew", Description = "Heals", EffectType = "HealOverTime", ValuePerStack = 0, IsPositive = true }
                ]
            })));
        }

        public GameDbContext Db { get; }
        public Room Room { get; }
        public Monster Monster { get; }
        public Character Character { get; }
        public IReadOnlyList<BattleParticipant> Participants { get; }
        public MonsterCombatService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-periodic-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5 };
            var monster = new Monster { Id = 1, RoomId = 1, Name = "Slime", Element = ElementType.Wind,
                Hp = 50, MaxHp = 50, Attack = 10, Defense = 2 };
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20 };
            var slot = new RoomSlot { Id = 1, RoomId = 1, SlotIndex = 1, CharacterId = 1, UserId = 1 };
            db.AddRange(room, monster, character, slot);
            await db.SaveChangesAsync();
            return new Fixture(path, db, room, monster, character, slot);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
