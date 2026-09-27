using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class PartyHpScalingTests
{
    [Theory]
    [InlineData(1, 10000)]
    [InlineData(2, 18000)]
    [InlineData(3, 26000)]
    [InlineData(4, 34000)]
    [InlineData(5, 42000)]
    public async Task FormationCountsCharactersOwnedByTheSameUser(int size, int expectedHp)
    {
        await using var test = await Scenario.CreateAsync();
        for (var slot = 2; slot <= size; slot++) await test.AssignOwnAsync(slot);
        Assert.Equal(expectedHp, test.Monster.Hp);
        Assert.Equal(expectedHp, test.Monster.MaxHp);
        Assert.Equal(10000, test.Monster.BaseMaxHp);
        Assert.Equal(size, test.Room.ScalingPartySize);
        Assert.Equal((1, 0), (test.Monster.Attack, test.Monster.Defense));
        var detail = await test.Rooms.GetRoomDetailAsync(1, "owner-token");
        Assert.True(detail!.IsPartyHpScaled);
        Assert.Equal(expectedHp, detail.MonsterMaxHp);
        var preview = await test.Rooms.GetDungeonAsync(1, "owner-token");
        Assert.Equal(10000, preview!.MonsterMaxHp);
        Assert.Equal(10000, Assert.Single(preview.Monsters).MaxHp);
        Assert.Equal(new[] { 100, 180, 260, 340, 420 }, preview.PartyHpPercentages);
    }

    [Fact]
    public async Task PreBattleRemovalLowersHpAndReassignmentDoesNotCompoundTheMultiplier()
    {
        await using var test = await Scenario.CreateAsync();
        var character = await test.AssignOwnAsync(2);
        Assert.Null((await test.Rooms.RemoveSlotAsync(1, 2, "owner-token")).Error);
        Assert.Equal((10000, 10000, 1), (test.Monster.Hp, test.Monster.MaxHp, test.Room.ScalingPartySize));
        Assert.Null((await test.Rooms.AssignSlotAsync(1,
            new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = character.Id }, "owner-token")).Error);
        Assert.Equal((18000, 18000, 2), (test.Monster.Hp, test.Monster.MaxHp, test.Room.ScalingPartySize));
    }

    [Theory]
    [InlineData(9000, 1000, 1800)]
    [InlineData(9999, 1, 2)]
    public async Task MidBattleJoinKeepsRemainingPercentageAndBothCharactersReceiveOriginalRewards(
        int attack, int remainingBefore, int remainingAfter)
    {
        await using var test = await Scenario.CreateAsync(attack: attack);
        Assert.Null((await test.Battle.StartPreparationAsync(1, "owner-token")).Error);
        Assert.Equal(remainingBefore, test.Monster.Hp);
        var (joined, error) = await test.Rooms.JoinRoomAsync(1, new JoinRoomRequest { SlotIndex = 2 }, "guest-token");
        Assert.Null(error);
        Assert.Equal(remainingAfter, joined!.MonsterHp);
        Assert.Equal(18000, joined.MonsterMaxHp);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Empty(await test.Db.RewardEntries.ToListAsync());
        await test.Scaling.SynchronizeAsync(test.Room);
        await test.Scaling.SynchronizeAsync(test.Room);
        Assert.Equal(remainingAfter, test.Monster.Hp);
        Assert.Null((await test.Rooms.LeaveRoomAsync(1, "guest-token")).Detail);
        Assert.Equal("RoomLocked", (await test.Rooms.LeaveRoomAsync(1, "guest-token")).Error);
        await test.FinishCooldownAsync("owner-token", "guest-token");
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Equal(0, test.Monster.Hp);
        Assert.Equal(10, test.Owner.Experience);
        Assert.Equal(10, test.Guest.Experience);
        Assert.Equal(13, test.Owner.Gold);
        Assert.Equal(13, test.Guest.Gold);
        Assert.Equal(2, await test.Db.CharacterItemStacks.CountAsync(stack => stack.ItemCode == "minor-healing-potion"));
    }

    [Theory]
    [InlineData("Dungeon", "fixed", 10000)]
    [InlineData("Hunt", "fixed", 10000)]
    [InlineData("Elite", "hunt-hp", 18000)]
    [InlineData("Dungeon", "hunt-hp", 18000)]
    public async Task ExplicitProfileControlsSingleBossRegardlessOfContentKind(string kind, string profile, int hp)
    {
        await using var test = await Scenario.CreateAsync(kind: kind, profile: profile);
        test.Monster.IsBoss = true;
        await test.AssignOwnAsync(2);
        Assert.Equal(hp, test.Monster.MaxHp);
        Assert.Equal(hp, test.Monster.Hp);
    }

    [Fact]
    public async Task DeadOrOfflineMemberDoesNotLowerHpAndFormationRemainsLockedDuringBattle()
    {
        await using var test = await Scenario.CreateAsync();
        var other = await test.AssignOwnAsync(2);
        other.Hp = 0;
        (await test.Db.RoomSlots.SingleAsync(slot => slot.SlotIndex == 2)).LastSeenAtUtc = DateTime.UtcNow.AddHours(-1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Battle.StartPreparationAsync(1, "owner-token")).Error);
        Assert.Equal(18000, test.Monster.MaxHp);
        Assert.Equal(2, test.Room.ScalingPartySize);
        Assert.Equal("FormationLocked", (await test.Rooms.RemoveSlotAsync(1, 2, "owner-token")).Error);
    }

    [Fact]
    public async Task ManualResetRecalculatesFromBaseHealthAfterMemberRemoval()
    {
        await using var test = await Scenario.CreateAsync(attack: 20000);
        await test.AssignOwnAsync(2);
        Assert.Null((await test.Battle.StartPreparationAsync(1, "owner-token")).Error);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Null((await test.Rooms.RemoveSlotAsync(1, 2, "owner-token")).Error);
        Assert.Equal(18000, test.Monster.MaxHp);
        Assert.True((await test.Battle.ResetBattleAsync(1, "owner-token")).Success);
        Assert.Equal((10000, 10000, 1), (test.Monster.Hp, test.Monster.MaxHp, test.Room.ScalingPartySize));
    }

    [Fact]
    public async Task VictoryWaitAdmissionDoesNotReviveMonsterAndRepeatUsesLatestFormation()
    {
        await using var test = await Scenario.CreateAsync(attack: 20000);
        test.Room.IsRepeatBattle = true;
        test.Room.ExpiresAtUtc = DateTime.UtcNow.AddHours(1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Battle.StartPreparationAsync(1, "owner-token")).Error);
        Assert.Null((await test.Rooms.JoinRoomAsync(1, new JoinRoomRequest { SlotIndex = 2 }, "guest-token")).Error);
        Assert.Equal((0, 10000, 1), (test.Monster.Hp, test.Monster.MaxHp, test.Room.ScalingPartySize));
        await test.RespawnAsync();
        Assert.Equal((18000, 18000, 2), (test.Monster.Hp, test.Monster.MaxHp, test.Room.ScalingPartySize));
        await test.FinishCooldownAsync("owner-token", "guest-token");
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Null((await test.Rooms.LeaveRoomAsync(1, "guest-token")).Error);
        Assert.Equal((0, 18000), (test.Monster.Hp, test.Monster.MaxHp));
        await test.RespawnAsync();
        Assert.Equal((10000, 10000, 1), (test.Monster.Hp, test.Monster.MaxHp, test.Room.ScalingPartySize));
        Assert.Equal(3, test.Room.RunSequence);
    }

    [Fact]
    public async Task ConcurrentRoundChangeRollsBackAdmissionAndHealthScalingTogether()
    {
        await using var test = await Scenario.CreateAsync(attack: 9000);
        await test.Battle.StartPreparationAsync(1, "owner-token");
        await using var concurrent = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(test.Connection).Options);
        var room = (await concurrent.Rooms.FindAsync(1))!;
        var monster = (await concurrent.Monsters.FindAsync(1))!;
        room.Version++;
        monster.Hp = 900;
        await concurrent.SaveChangesAsync();
        var (detail, error) = await test.Rooms.JoinRoomAsync(1, new JoinRoomRequest { SlotIndex = 2 }, "guest-token");
        Assert.Null(detail);
        Assert.Equal("ConcurrencyConflict", error);
        await concurrent.Entry(monster).ReloadAsync();
        Assert.Equal((900, 10000), (monster.Hp, monster.MaxHp));
        Assert.Null((await concurrent.RoomSlots.SingleAsync(slot => slot.SlotIndex == 2)).CharacterId);
        Assert.False(await concurrent.CharacterActivities.AnyAsync(activity => activity.CharacterId == test.Guest.Id));
    }

    [Fact]
    public void ProductionContentExplicitlySelectsHuntOrFixedProfiles()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var catalog = new PartyScalingCatalog(Options.Create(config.GetSection(PartyScalingOptions.SectionName).Get<PartyScalingOptions>()!));
        var world = WorldCatalog.LoadDefault();
        Assert.All(world.Dungeons.Where(dungeon => dungeon.IsVisible && dungeon.DungeonKind is "Hunt" or "Elite"),
            dungeon => Assert.Equal("hunt-hp", dungeon.PartyScalingProfileCode));
        Assert.All(world.Dungeons.Where(dungeon => dungeon.DungeonKind is "Dungeon" or "Legacy"),
            dungeon => Assert.Equal("fixed", dungeon.PartyScalingProfileCode));
        Assert.All(world.Dungeons, dungeon => Assert.True(catalog.HasProfile(dungeon.PartyScalingProfileCode)));
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private Scenario(SqliteConnection connection, GameDbContext db, Room room, Monster monster, Character owner, Character guest)
        {
            Connection = connection;
            Db = db;
            Room = room;
            Monster = monster;
            Owner = owner;
            Guest = guest;
            Scaling = new(db, PartyScalingCatalog.Default);
            var progression = ProgressionTestFactory.Create();
            var skills = SkillTestFactory.Create();
            var consumables = ConsumableTestFactory.Create();
            var users = new UserService(db, progression, skills);
            var rewards = RewardTestFactory.CreateService(db, progression);
            Rooms = new(db, users, progression, consumables, skills, rewards, partyScalingService: Scaling);
            Battle = new(db, users, consumables, skills, rewards, partyScalingService: Scaling);
        }

        public SqliteConnection Connection { get; }
        public GameDbContext Db { get; }
        public Room Room { get; }
        public Monster Monster { get; }
        public Character Owner { get; }
        public Character Guest { get; }
        public RoomService Rooms { get; }
        public BattleService Battle { get; }
        public PartyScalingService Scaling { get; }

        public static async Task<Scenario> CreateAsync(int attack = 20, string kind = "Hunt", string profile = "hunt-hp")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var owner = new Character { Id = 1, UserId = 1, Name = "Owner", ProfessionCode = "knight", Attack = attack, MaxHp = 100, Hp = 100 };
            var guest = new Character { Id = 2, UserId = 2, Name = "Guest", ProfessionCode = "knight", Attack = 20, MaxHp = 100, Hp = 100 };
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5,
                Status = RoomStatus.NotStarted, IsPublic = true, IsPreparationTimeoutEnabled = false };
            var monster = new Monster { Id = 1, RoomId = 1, Name = "Target", BaseMaxHp = 10000, MaxHp = 10000, Hp = 10000, Attack = 1 };
            db.AddRange(owner, guest, room, monster,
                new Dungeon { Id = 1, Code = "slime-field", Name = "Target", DungeonKind = kind,
                    PartyScalingProfileCode = profile, MonsterName = "Target", MonsterMaxHp = 10000, MonsterAttack = 1 },
                new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 2 },
                new UserLoginSession { UserId = 1, Token = "owner-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                new UserLoginSession { UserId = 2, Token = "guest-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            db.RoomSlots.AddRange(Enumerable.Range(1, 5).Select(index => new RoomSlot
            {
                RoomId = 1, SlotIndex = index, CharacterId = index == 1 ? 1 : null,
                UserId = index == 1 ? 1 : null, IsMainControl = index == 1
            }));
            await db.SaveChangesAsync();
            return new(connection, db, room, monster, owner, guest);
        }

        public async Task<Character> AssignOwnAsync(int slot)
        {
            var character = new Character { UserId = 1, Name = $"Own-{slot}", ProfessionCode = "knight", Attack = 20, MaxHp = 100, Hp = 100 };
            Db.Characters.Add(character);
            await Db.SaveChangesAsync();
            Assert.Null((await Rooms.AssignSlotAsync(1, new AssignRoomSlotRequest { SlotIndex = slot, CharacterId = character.Id }, "owner-token")).Error);
            return character;
        }

        public async Task FinishCooldownAsync(params string[] tokens)
        {
            foreach (var token in tokens) Assert.Null((await Battle.StartPreparationAsync(1, token)).Error);
            Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await Db.SaveChangesAsync();
            Assert.Null((await Battle.SyncRoomAsync(1)).Error);
        }

        public async Task RespawnAsync()
        {
            Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.RepeatBattleDelaySeconds - 1);
            await Db.SaveChangesAsync();
            Assert.Null((await Battle.SyncRoomAsync(1)).Error);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
