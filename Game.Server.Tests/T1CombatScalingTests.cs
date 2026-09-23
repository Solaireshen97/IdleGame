using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class T1CombatScalingTests
{
    private static IConfiguration Production() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();

    [Fact]
    public void SkillCoefficientPrecedesFlatPowerAndRoundsOnlyAfterAllZones()
    {
        // ((101 * .6 + 4) * 1.2 - 7) * 1.5 = 105.78
        Assert.Equal(105, DamageCalculator.Calculate(101, 7, 4,
            new DamageFactors(AttackPercent: 20, SkillDamagePercent: 50), 60));
        Assert.Equal(68, RecoveryCalculator.Calculate(605, 20, 8));
        Assert.Equal(int.MaxValue, RecoveryCalculator.Calculate(int.MaxValue, int.MaxValue, 100));
    }

    [Fact]
    public void HuntPreviewAndRealEncounterUseTheSameCalibratedStats()
    {
        var encounters = new DungeonEncounterCatalog(Options.Create(Production()
            .GetSection(DungeonEncounterOptions.SectionName).Get<DungeonEncounterOptions>()!),
            new MonsterCombatCatalog(Options.Create(Production().GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!)));
        foreach (var dungeon in WorldCatalog.LoadDefault().Dungeons.Where(d => d.IsVisible && d.DungeonKind == "Hunt"))
        {
            var monster = Assert.Single(encounters.CreateMonsters(dungeon));
            Assert.Equal((dungeon.MonsterMaxHp, dungeon.MonsterAttack, dungeon.MonsterDefense),
                (monster.MaxHp, monster.Attack, monster.Defense));
        }
    }

    [Fact]
    public async Task ActualRoundAndPotionPreviewUseEffectiveHpAndScaledDamage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var character = new Character { Id = 1, UserId = 1, Name = "测试祭司", ProfessionCode = "acolyte",
            Attack = 100, Hp = 100, MaxHp = 500, WeaponHealthBonusPercent = 20,
            WeaponAttackBonusPercent = 20, WeaponSkillDamagePercent = 50 };
        db.AddRange(new User { Id = 1, UserName = "scaling-test", PasswordHash = "x", ActiveCharacterId = 1 }, character,
            new UserLoginSession { UserId = 1, Token = "test", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
            new Dungeon { Id = 1, Code = "slime-field", Name = "测试场", MonsterName = "木桩", MonsterMaxHp = 10000, MonsterAttack = 1, SlotCount = 5 },
            new Monster { Id = 1, Name = "木桩", MaxHp = 10000, Hp = 10000, Attack = 1 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true },
            new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
            new CharacterItemStack { CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 2 },
            new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 1, ItemCode = "minor-healing-potion", AutoUseEnabled = true, AutoHpThresholdPercent = 80 },
            new CharacterSkillSlot { CharacterId = 1, SlotIndex = 1, SkillCode = "acolyte-heal", AutoUseEnabled = true, AutoHpThresholdPercent = 80 },
            new CharacterSkillSlot { CharacterId = 1, SlotIndex = 2, SkillCode = "acolyte-holy-bolt", AutoUseEnabled = true });
        await db.SaveChangesAsync();
        var config = Production();
        var skills = new SkillCatalog(Options.Create(config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!));
        var potions = new ConsumableCatalog(Options.Create(config.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!));
        var progression = ProgressionTestFactory.Create();
        var users = new UserService(db, progression, skills);
        var (preview, previewError) = await new ConsumableService(db, users, potions).GetAsync("test", 1);
        Assert.Null(previewError);
        Assert.Equal(68, preview!.Items.Single(item => item.Code == "minor-healing-potion").HealAmount);
        var battle = new BattleService(db, users, potions, skills, RewardTestFactory.CreateService(db, progression));
        var (result, error) = await battle.StartPreparationAsync(1, "test");
        Assert.Null(error);
        Assert.Equal(9693, result!.MonsterHp); // 120 normal + 187 skill
        Assert.Equal(243, character.Hp); // 100 + 76 heal + 68 potion - 1 incoming
        Assert.Equal(1, (await db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(2, await db.BattleSkillCooldowns.CountAsync());
        Assert.Single(await db.BattleConsumableCooldowns.ToListAsync());
    }
}
