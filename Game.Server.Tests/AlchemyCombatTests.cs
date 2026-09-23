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

public sealed class AlchemyCombatTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();

    [Fact]
    public void TenLevelTiersScaleAllPotionEffects()
    {
        Assert.Equal(1, ConsumableRules.TierForLevel(10));
        Assert.Equal(2, ConsumableRules.TierForLevel(11));
        Assert.Equal(100, ConsumableRules.EffectScalePercent(1, 10));
        Assert.Equal(75, ConsumableRules.EffectScalePercent(1, 11));
        Assert.Equal(25, ConsumableRules.EffectScalePercent(1, 21));
        Assert.Equal(0, ConsumableRules.EffectScalePercent(1, 31));
        Assert.Equal(3, ConsumableRules.ScaledSkillLevel(3, 1, 10));
        Assert.Equal(2, ConsumableRules.ScaledSkillLevel(3, 1, 11));
        Assert.Equal(1, ConsumableRules.ScaledSkillLevel(3, 1, 21));
        Assert.Equal(0, ConsumableRules.ScaledSkillLevel(3, 1, 31));
    }

    [Theory]
    [InlineData("elwynn-assault-draught", 24, 20)]
    [InlineData("durotar-bloodfire-draught", 23, 21)]
    [InlineData("mulgore-hunter-draught", 22, 20)]
    public async Task RegionalOperationPotionsApplyDistinctDamageAndRisk(string potionCode,
        int expectedDamage, int expectedDamageTaken)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var config = Configuration();
        var consumables = new ConsumableCatalog(Options.Create(
            config.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!));
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var character = new Character { Id = 1, UserId = 1, Name = "测试角色", Level = 1,
            Hp = 100, MaxHp = 100, Attack = 20 };
        db.AddRange(new User { Id = 1, UserName = "alchemy", PasswordHash = "x", ActiveCharacterId = 1 }, character,
            new UserLoginSession { UserId = 1, Token = "test", CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow.AddDays(1) },
            new Dungeon { Id = 1, Code = "training", Name = "训练场", MonsterName = "木桩",
                MonsterMaxHp = 1000, MonsterAttack = 20, SlotCount = 5 },
            new Monster { Id = 1, Name = "木桩", Hp = 1000, MaxHp = 1000, Attack = 20 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5,
                Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true },
            new CharacterBattleMilestone { CharacterId = 1, Kind = BattleMilestoneService.DungeonClearKind,
                TargetCode = "training", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow },
            new CharacterItemStack { CharacterId = 1, ItemCode = potionCode, Quantity = 2 },
            new CharacterConsumableSlot { CharacterId = 1,
                SlotIndex = ConsumableRules.OperationPotionSlotIndex, ItemCode = potionCode,
                AutoHpThresholdPercent = 50 });
        await db.SaveChangesAsync();
        var users = new UserService(db, progression, skills);
        var rewards = RewardTestFactory.CreateService(db, progression);
        var battle = new BattleService(db, users, consumables, skills, rewards);
        var first = await battle.StartPreparationAsync(1, "test");
        Assert.Null(first.Error);
        Assert.Equal(1000 - expectedDamage, first.Result!.MonsterHp);
        Assert.Equal(100 - expectedDamageTaken, character.Hp);
        Assert.Equal(1, (await db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.True((await new RoomService(db, users, progression, consumables, skills, rewards)
            .GetRoomDetailAsync(1, "test"))!.Slots.Single().OperationPotion!.IsActive);
    }

    [Theory]
    [InlineData("whetstone-oil", false)]
    [InlineData("dun-morogh-fortitude-draught", true)]
    public async Task CombatBuffUsesWeaponSkillCurveAndCanBeReusedAfterCooldown(string potionCode, bool increasesMaxHp)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var config = Configuration();
        var weapons = new WeaponCatalog(Options.Create(config.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
        var consumables = new ConsumableCatalog(Options.Create(config.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!), weapons);
        var skills = SkillTestFactory.Create();
        var progression = ProgressionTestFactory.Create();
        var character = new Character { Id = 1, UserId = 1, Name = "测试剑士", Attack = 20, Hp = 200,
            MaxHp = 200, Level = 1 };
        var mainWeapon = weapons.CreateStarterWeapons(1, "swordsman").First();
        weapons.ApplyBonuses(character, [mainWeapon]);
        character.Hp = TalentRules.EffectiveMaxHp(character);
        var initialMaxHp = character.Hp;
        db.AddRange(new User { Id = 1, UserName = "alchemy", PasswordHash = "x", ActiveCharacterId = 1 }, character,
            new UserLoginSession { UserId = 1, Token = "test", CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow.AddDays(1) },
            new Dungeon { Id = 1, Code = "training", Name = "训练场", MonsterName = "木桩", MonsterMaxHp = 10000,
                MonsterAttack = 1, SlotCount = 5 },
            new Monster { Id = 1, Name = "木桩", Hp = 10000, MaxHp = 10000, Attack = 1 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5,
                Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true },
            new CharacterBattleMilestone { CharacterId = 1, Kind = BattleMilestoneService.DungeonClearKind,
                TargetCode = "training", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow },
            new CharacterItemStack { CharacterId = 1, ItemCode = potionCode, Quantity = 2 },
            new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 1, ItemCode = potionCode,
                AutoHpThresholdPercent = 50 });
        db.CharacterWeapons.Add(mainWeapon);
        await db.SaveChangesAsync();

        var users = new UserService(db, progression, skills);
        var rewards = RewardTestFactory.CreateService(db, progression);
        var battle = new BattleService(db, users, consumables, skills, rewards, weaponCatalog: weapons);
        var rooms = new RoomService(db, users, progression, consumables, skills, rewards, weaponCatalog: weapons);
        var queued = await battle.QueueConsumableAsync(new QueueConsumableRequest
        {
            RoomId = 1, CharacterId = 1, ConsumableSlotIndex = 1
        }, "test");
        Assert.True(queued.Success);
        var first = await battle.StartPreparationAsync(1, "test");
        Assert.Null(first.Error);
        Assert.Contains(first.Result!.Logs, log => log.Contains($"使用 {consumables.FindItem(potionCode)!.Name}"));
        Assert.Equal(1, (await db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.True(character.TemporaryWeaponAttackBonusPercent > 0);
        Assert.Equal(increasesMaxHp, TalentRules.EffectiveMaxHp(character) > initialMaxHp);
        var active = Assert.Single((await rooms.GetRoomDetailAsync(1, "test"))!.Slots.Single().StatusEffects,
            effect => effect.Code == $"combat-consumable:{potionCode}");
        Assert.Equal(2, active.RemainingRounds);

        var room = await db.Rooms.SingleAsync();
        for (var round = 1; round <= 6; round++)
        {
            room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            if (round == 6)
                Assert.True((await battle.QueueConsumableAsync(new QueueConsumableRequest
                { RoomId = 1, CharacterId = 1, ConsumableSlotIndex = 1 }, "test")).Success);
            var result = await battle.StartPreparationAsync(1, "test");
            Assert.Null(result.Error);
            if (round == 3)
            {
                Assert.Equal(0, character.TemporaryWeaponAttackBonusPercent);
                Assert.Equal(initialMaxHp, TalentRules.EffectiveMaxHp(character));
                Assert.True(character.Hp <= initialMaxHp);
                Assert.DoesNotContain((await rooms.GetRoomDetailAsync(1, "test"))!.Slots.Single().StatusEffects,
                    effect => effect.Code == $"combat-consumable:{potionCode}");
            }
        }
        Assert.Equal(0, (await db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(2, await db.BattleConsumableBuffs.CountAsync());
    }
}
