using Game.Server.Data;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public class RoomServiceTests
{
    [Fact]
    public async Task CreateRoomAsync_ConfiguredEncounterPersistsEveryWaveAndSelectsFirstEnemy()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var progression = ProgressionTestFactory.Create();
        var encounters = new DungeonEncounterCatalog(Options.Create(new DungeonEncounterOptions
        {
            Dungeons = new Dictionary<string, List<DungeonWaveOptions>>(StringComparer.OrdinalIgnoreCase)
            {
                ["slime-field"] =
                [
                    new() { Monsters = [new() { Name = "Slime A", MaxHp = 30, Attack = 5, Defense = 1, RewardProfileCode = "slime-a" }] },
                    new() { Monsters = [new() { Name = "Slime B", MaxHp = 60, Attack = 9, Defense = 2, RewardProfileCode = "slime-b", IsBoss = true }] }
                ]
            }
        }));
        var service = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(),
            RewardTestFactory.CreateService(test.Db, progression), encounters);

        var (detail, error) = await service.CreateRoomAsync("Slime", test.Token);

        Assert.Null(error);
        Assert.Equal((1, 2, "Slime A"), (detail!.CurrentWaveNumber, detail.TotalWaveCount, detail.MonsterName));
        var monsters = await test.Db.Monsters.OrderBy(monster => monster.WaveNumber).ToListAsync();
        Assert.Equal(2, monsters.Count);
        Assert.All(monsters, monster => Assert.Equal(detail.RoomId, monster.RoomId));
        Assert.Equal(new[] { 1, 2 }, monsters.Select(monster => monster.WaveNumber));
        Assert.Equal(new[] { "slime-a", "slime-b" }, monsters.Select(monster => monster.RewardProfileCode));
        Assert.True(monsters[1].IsBoss);
    }

    [Fact]
    public async Task CreateRoomAsync_StoresRepeatBattleChoice()
    {
        await using var test = await RoomTestContext.CreateAsync();

        var (detail, error) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isRepeatBattle: true);

        Assert.Null(error);
        Assert.True(detail!.IsRepeatBattle);
        Assert.True((await test.Db.Rooms.FindAsync(detail.RoomId))!.IsRepeatBattle);
    }

    [Fact]
    public async Task CreateRoomAsync_InitializesDefaultDungeonsAndUsesSelectedDungeon()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (first, firstError) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var dungeons = await test.Db.Dungeons.OrderBy(dungeon => dungeon.SortOrder).ToListAsync();
        var (second, secondError) = await test.Service.CreateRoomAsync(dungeons.Single(dungeon => dungeon.Code == "goblin-camp").Id, null, test.Token);

        Assert.Null(firstError);
        Assert.Equal(64, dungeons.Count);
        Assert.Equal("northshire-wolves", dungeons[0].Code);
        Assert.Equal(61, dungeons.Count(dungeon => dungeon.IsVisible));
        Assert.False(dungeons.Single(dungeon => dungeon.Code == "slime-field").IsVisible);
        Assert.Equal(8, dungeons.Single(dungeon => dungeon.Code == "kobold-mine").MinimumLevel);
        Assert.Null(second);
        Assert.Equal("CharacterAlreadyInRoom", secondError);
        Assert.Equal(dungeons.Single(dungeon => dungeon.Code == "slime-field").Id, first!.DungeonId);
        Assert.Equal(ElementType.Wind, first.MonsterElement);
    }

    [Fact]
    public async Task DungeonList_ExposesProgressionAndCreateRoomEnforcesMinimumLevel()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(test.Db);

        var dungeons = await test.Service.GetDungeonsAsync(test.Token);
        var mine = Assert.Single(dungeons, dungeon => dungeon.Code == "kobold-mine");
        var firstHunt = Assert.Single(dungeons, dungeon => dungeon.Code == "northshire-wolves");
        var (room, error) = await test.Service.CreateRoomAsync(mine.DungeonId, null, test.Token);

        Assert.Equal(61, dungeons.Count);
        Assert.True(firstHunt.CanEnter);
        Assert.False(mine.CanEnter);
        Assert.Equal("需要角色达到 Lv.8", mine.LockReason);
        Assert.Null(room);
        Assert.Equal("CharacterLevelTooLow", error);
    }

    [Fact]
    public async Task CreateRoomAsync_StoresPreparationTimeoutChoice()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, error) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPreparationTimeoutEnabled: false);

        Assert.Null(error);
        Assert.False(detail!.IsMixedTeam);
        Assert.False(detail.IsPreparationTimeoutEnabled);
        Assert.Null(detail.PreparationExpiresAtUtc);
        Assert.False((await test.Db.Rooms.FindAsync(detail.RoomId))!.IsPreparationTimeoutEnabled);
    }

    [Fact]
    public async Task CreateRoomAsync_WithPreparationTimeoutStartsThirtySecondIdleCountdown()
    {
        await using var test = await RoomTestContext.CreateAsync();

        var (detail, error) = await test.Service.CreateRoomAsync("Slime", test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.NotStarted, detail!.RoomStatus);
        Assert.InRange((detail.PreparationExpiresAtUtc!.Value - detail.ServerTimeUtc).TotalSeconds, 29, 31);
    }

    [Fact]
    public async Task CreateRoomAsync_MixedTeamRespectsPreparationTimeoutChoice()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPreparationTimeoutEnabled: false);
        await test.AddOtherActiveCharacterAsync();
        var (detail, error) = await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        Assert.Null(error);
        Assert.True(detail!.IsMixedTeam);
        Assert.False(detail.IsPreparationTimeoutEnabled);
    }

    [Fact]
    public async Task CreateRoomAsync_CreatesFiveSlotsAndMainControl()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, error) = await test.Service.CreateRoomAsync("Slime", test.Token);

        Assert.Null(error);
        Assert.NotNull(detail);
        Assert.Equal(5, detail!.Slots.Count);
        var firstSlot = Assert.Single(detail.Slots, x => x.SlotIndex == 1);
        Assert.Equal(test.ActiveCharacter.Id, firstSlot.CharacterId);
        Assert.True(firstSlot.IsMainControl);
    }

    [Fact]
    public async Task AssignSlotAsync_AssignsOwnedCharacter()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var character = await test.AddCharacterAsync("Mage");

        var (updated, error) = await test.Service.AssignSlotAsync(detail!.RoomId, new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = character.Id }, test.Token);

        Assert.Null(error);
        Assert.Equal(character.Id, updated!.Slots.Single(x => x.SlotIndex == 2).CharacterId);
    }

    [Fact]
    public async Task AssignSlotAsync_RejectsOtherUsersCharacter()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var otherCharacter = await test.AddOtherCharacterAsync();

        var (updated, error) = await test.Service.AssignSlotAsync(detail!.RoomId, new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = otherCharacter.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("NotCharacterOwner", error);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(x => x.RoomId == detail.RoomId && x.SlotIndex == 2)).CharacterId);
    }

    [Fact]
    public async Task JoinRoomAsync_EmptySlot_AddsOtherUsersActiveCharacter()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        await test.AddOtherActiveCharacterAsync();

        var (detail, error) = await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        Assert.Null(error);
        var slot = detail!.Slots.Single(x => x.SlotIndex == 2);
        Assert.Equal(2, slot.CharacterId);
        Assert.Equal("other", slot.PlayerName);
    }

    [Fact]
    public async Task JoinRoomAsync_OccupiedOrLockedSlot_IsRejected()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        await test.AddOtherActiveCharacterAsync();

        var (_, occupiedError) = await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 1 }, "other-token");
        var entity = await test.Db.Rooms.FindAsync(room.RoomId);
        entity!.Status = RoomStatus.Preparing;
        await test.Db.SaveChangesAsync();
        var (_, lockedError) = await test.Service.JoinRoomAsync(room.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        Assert.Equal("SlotOccupied", occupiedError);
        Assert.Equal("RoomLocked", lockedError);
    }

    [Fact]
    public async Task AssignSlotAsync_DuringCooldown_DoesNotChangeSlots()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var character = await test.AddCharacterAsync("Mage");
        var room = await test.Db.Rooms.FindAsync(detail!.RoomId);
        room!.Status = RoomStatus.Cooldown;
        await test.Db.SaveChangesAsync();

        var (updated, error) = await test.Service.AssignSlotAsync(detail.RoomId, new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = character.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("FormationLocked", error);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(x => x.RoomId == detail.RoomId && x.SlotIndex == 2)).CharacterId);
    }

    private sealed class RoomTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private RoomTestContext(string path, GameDbContext db, Character activeCharacter)
        {
            _path = path;
            Db = db;
            ActiveCharacter = activeCharacter;
            var progression = ProgressionTestFactory.Create();
            Service = new RoomService(db, new UserService(db, progression, SkillTestFactory.Create()), progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(db, progression));
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Character ActiveCharacter { get; }
        public RoomService Service { get; }

        public static async Task<RoomTestContext> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-room-tests-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var activeCharacter = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20, Defense = 5 };
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 }, activeCharacter, new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return new RoomTestContext(path, db, activeCharacter);
        }

        public async Task<Character> AddCharacterAsync(string name)
        {
            var character = new Character { UserId = 1, Name = name, Hp = 100, MaxHp = 100, Attack = 20, Defense = 5 };
            Db.Characters.Add(character);
            await Db.SaveChangesAsync();
            return character;
        }

        public async Task AddOtherActiveCharacterAsync()
        {
            Db.AddRange(
                new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 },
                new Character { Id = 2, UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20, Defense = 5 },
                new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await Db.SaveChangesAsync();
        }

        public async Task<Character> AddOtherCharacterAsync()
        {
            var character = new Character { UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20, Defense = 5 };
            Db.AddRange(new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = null }, character);
            await Db.SaveChangesAsync();
            return character;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
