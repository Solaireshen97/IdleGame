using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonRewardMaterialTests
{
    [Fact]
    public async Task FirstClearGrantsBonusTokensOnlyOnceAndRepeatClearStillGrantsOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-dungeon-token-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var character = new Character
            {
                Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20, Defense = 5
            };
            var dungeon = new Dungeon
            {
                Id = 1, Code = "kobold-mine", Name = "狗头人矿洞", MonsterName = "金牙",
                MonsterMaxHp = 100, MonsterAttack = 10, MonsterDefense = 3
            };
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                character, dungeon);
            await db.SaveChangesAsync();
            var rewards = CreateRewards(db);
            var service = new DungeonRunService(db, rewards);

            var firstRoom = await AddCompletedRoomAsync(db, dungeon.Id, 1);
            await service.AdvanceAfterDefeatAsync(firstRoom.Room, firstRoom.Monster,
                [new RewardParticipant(1, character)], DateTime.UtcNow, []);
            await db.SaveChangesAsync();

            Assert.Equal(3, (await db.CharacterItemStacks.SingleAsync()).Quantity);
            Assert.Single(await db.UserDungeonClears.ToListAsync());

            var repeatRoom = await AddCompletedRoomAsync(db, dungeon.Id, 2);
            await service.AdvanceAfterDefeatAsync(repeatRoom.Room, repeatRoom.Monster,
                [new RewardParticipant(1, character)], DateTime.UtcNow, []);
            await db.SaveChangesAsync();

            Assert.Equal(4, (await db.CharacterItemStacks.SingleAsync()).Quantity);
            Assert.Single(await db.UserDungeonClears.ToListAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static RewardService CreateRewards(GameDbContext db)
    {
        var materials = new MaterialCatalog(Options.Create(new MaterialOptions
        {
            Items = [new MaterialItemOptions
            {
                Code = "kobold-mine-token", Name = "矿洞徽记", Description = "测试材料。"
            }]
        }));
        var weapons = new WeaponCatalog(Options.Create(new WeaponOptions
        {
            Items = [new WeaponTemplateOptions
            {
                Code = "starter", Name = "Starter", Element = ElementType.Fire, Attack = 1, MaxHp = 1
            }],
            StarterPacks = new Dictionary<string, List<string>> { ["knight"] = ["starter"] }
        }));
        var catalog = new RewardCatalog(Options.Create(new RewardOptions
        {
            MonsterKills = new Dictionary<string, RewardBundleOptions>
            {
                ["kobold-mine"] = new()
            },
            DungeonClears = new Dictionary<string, RewardBundleOptions>
            {
                ["kobold-mine"] = new()
                {
                    Drops = [new RewardDropOptions
                    {
                        Kind = "Material", Code = "kobold-mine-token", Quantity = 1
                    }]
                },
                ["kobold-mine-first-clear"] = new()
                {
                    Drops = [new RewardDropOptions
                    {
                        Kind = "Material", Code = "kobold-mine-token", Quantity = 2
                    }]
                }
            }
        }), ConsumableTestFactory.Create(), weapons, materials);
        return new RewardService(db, catalog, ProgressionTestFactory.Create());
    }

    private static async Task<(Room Room, Monster Monster)> AddCompletedRoomAsync(
        GameDbContext db, int dungeonId, int id)
    {
        var monster = new Monster
        {
            Id = id, RoomId = id, WaveNumber = 1, Position = 1, Name = "金牙",
            Hp = 0, MaxHp = 100, Attack = 10, Defense = 3, RewardProfileCode = "kobold-mine"
        };
        var room = new Room
        {
            Id = id, DungeonId = dungeonId, MonsterId = id, OwnerUserId = 1,
            SlotCount = 5, Status = RoomStatus.Preparing, CurrentWaveNumber = 1, TotalWaveCount = 1
        };
        db.AddRange(monster, room);
        await db.SaveChangesAsync();
        return (room, monster);
    }
}
