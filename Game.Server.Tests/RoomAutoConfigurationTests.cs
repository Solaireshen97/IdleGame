using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed class RoomAutoConfigurationTests
{
    [Fact]
    public async Task RoomDetailAllowsUnlockedMainCharacterToConfigureAuto()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-room-auto-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        var db = new GameDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(
                new Dungeon { Id = 1, Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterMaxHp = 35, MonsterAttack = 8, MonsterDefense = 2, SlotCount = 5, SortOrder = 1 },
                new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 45, MaxHp = 45, Attack = 20},
                new Monster { Id = 1, Name = "Slime", Hp = 35, MaxHp = 35, Attack = 8, Defense = 2 },
                new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted, RoundNumber = 3 },
                new RoomSlot { Id = 1, RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true },
                new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
                new CharacterBattleMilestone { CharacterId = 1, Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow },
                new UserLoginSession { Id = 1, UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();

            var progression = ProgressionTestFactory.Create();
            var skills = SkillTestFactory.Create();
            var service = new RoomService(db, new UserService(db, progression, skills), progression,
                ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression));

            var detail = await service.GetRoomDetailAsync(1, "token");

            var slot = Assert.Single(detail!.Slots, item => item.SlotIndex == 1);
            Assert.True(slot.IsAutoUnlockedForCurrentUser);
            Assert.True(slot.CanConfigureAuto);

            await db.CharacterBattleMilestones.ExecuteDeleteAsync();
            detail = await service.GetRoomDetailAsync(1, "token");
            slot = Assert.Single(detail!.Slots, item => item.SlotIndex == 1);
            Assert.False(slot.IsAutoUnlockedForCurrentUser);
            Assert.False(slot.CanConfigureAuto);
        }
        finally
        {
            await db.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DungeonAutoUnlockDoesNotCarryOverToAnotherCharacterOnSameAccount()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-room-auto-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        var db = new GameDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(
                new Dungeon { Id = 1, Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterMaxHp = 35, MonsterAttack = 8, SlotCount = 5, SortOrder = 1, IsVisible = true },
                new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 2 },
                new Character { Id = 1, UserId = 1, Name = "Veteran", Hp = 45, MaxHp = 45, Attack = 20 },
                new Character { Id = 2, UserId = 1, Name = "Newcomer", Hp = 45, MaxHp = 45, Attack = 20 },
                new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
                new CharacterBattleMilestone { CharacterId = 1, Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow },
                new UserLoginSession { Id = 1, UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            var progression = ProgressionTestFactory.Create();
            var skills = SkillTestFactory.Create();
            var service = new RoomService(db, new UserService(db, progression, skills), progression,
                ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression));

            Assert.False(Assert.Single(await service.GetDungeonsAsync("token")).AutoUnlocked);
            (await db.Users.SingleAsync()).ActiveCharacterId = 1;
            await db.SaveChangesAsync();
            Assert.True(Assert.Single(await service.GetDungeonsAsync("token")).AutoUnlocked);
        }
        finally
        {
            await db.DisposeAsync();
            File.Delete(path);
        }
    }
}
