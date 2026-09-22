using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class T1WeaponEffectTests
{
    internal static WeaponCatalog ProductionCatalog()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"));
        return new WeaponCatalog(Options.Create(new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
    }

    private static CharacterWeapon Weapon(int slot, params (string Code, int Level)[] skills) => new()
    {
        Element = ElementType.Fire, EquippedSlotIndex = slot, Attack = 20, MaxHp = 50,
        Skills = skills.Select((skill, index) => new CharacterWeaponSkill
        { SlotIndex = index + 1, SkillCode = skill.Code, BaseLevel = skill.Level, Level = skill.Level }).ToList()
    };

    [Fact]
    public void CompositeAndSingleSkillsShareOneEffectCurveAndIgnoreOffElementAndInventory()
    {
        var catalog = ProductionCatalog();
        var offElement = Weapon(3, ("weapon-attack", 20));
        offElement.Element = ElementType.Water;
        var inventory = Weapon(4, ("weapon-attack", 20));
        inventory.EquippedSlotIndex = null;
        var bonuses = catalog.CalculateBonuses([Weapon(1, ("weapon-attack", 10)),
            Weapon(2, ("weapon-might", 20)), offElement, inventory]);

        Assert.Equal(30, bonuses.AttackPercent); // effective level20, not 20%+20%
        Assert.Equal(30, bonuses.HealthPercent);
        Assert.Equal(20, bonuses.Effects.Single(effect => effect.EffectType == WeaponSkillEffectType.AttackPercent).EffectiveLevel);
        Assert.Equal(2, bonuses.ActiveSkills.Count);
        Assert.Empty(catalog.CalculateBonuses([offElement, inventory]).Effects);
    }

    [Theory]
    [InlineData(WeaponSkillEffectType.AttackPercent, 20, 30, 40, 60)]
    [InlineData(WeaponSkillEffectType.MaxHpPercent, 30, 45, 60, 75)]
    [InlineData(WeaponSkillEffectType.CriticalChancePercent, 30, 45, 60, 60)]
    [InlineData(WeaponSkillEffectType.StaminaPercent, 20, 30, 30, 30)]
    [InlineData(WeaponSkillEffectType.EnmityPercent, 40, 60, 60, 60)]
    [InlineData(WeaponSkillEffectType.DoubleAttackChancePercent, 20, 30, 40, 40)]
    [InlineData(WeaponSkillEffectType.NormalEchoPercent, 15, 22.5, 30, 30)]
    [InlineData(WeaponSkillEffectType.SkillDamagePercent, 40, 60, 80, 80)]
    public void ProductionEffectCurvesMatchT1Budget(WeaponSkillEffectType effect, double at10, double at20, double at40, double cap)
    {
        var catalog = ProductionCatalog();
        Assert.Equal((decimal)at10, catalog.CalculateEffectPercent(effect, 10));
        Assert.Equal((decimal)at20, catalog.CalculateEffectPercent(effect, 20));
        Assert.Equal((decimal)at40, catalog.CalculateEffectPercent(effect, 40));
        Assert.Equal((decimal)cap, catalog.CalculateEffectPercent(effect, 1000));
    }

    [Fact]
    public void FractionalCompositeLevelsCrossCurveBoundariesWithoutRounding()
    {
        var bonuses = ProductionCatalog().CalculateBonuses([
            Weapon(1, ("weapon-double", 10)), Weapon(2, ("weapon-momentum", 1))]);
        Assert.Equal(20.4m, bonuses.Percent(WeaponSkillEffectType.DoubleAttackChancePercent));
        Assert.Equal(.6m, bonuses.Percent(WeaponSkillEffectType.NormalEchoPercent));
    }

    [Theory]
    [InlineData(100, 30)]
    [InlineData(90, 18)]
    [InlineData(75, 0)]
    [InlineData(50, 0)]
    [InlineData(25, 30)]
    [InlineData(10, 48)]
    [InlineData(0, 0)]
    public void HealthEffectsHaveExclusiveThresholdsAndDeadCharactersGetNoBenefit(int hp, int expected) =>
        Assert.Equal(expected, WeaponCombatRules.HealthDamagePercent(hp, 100, 30, 60));

    [Fact]
    public void EchoUsesFinalDamageAndCanRoundDownToZero()
    {
        var hit = DamageCalculator.Calculate(100, 20, factors: new DamageFactors(
            CriticalPercent: 50, ElementPercent: 25, ReductionPercent: 20));
        Assert.Equal(120, hit);
        Assert.Equal(18, WeaponCombatRules.EchoDamage(hit, 15));
        Assert.Equal(0, WeaponCombatRules.EchoDamage(1, 30));
        Assert.Equal(0, WeaponCombatRules.EchoDamage(hit, -10));
    }

    [Theory]
    [InlineData(1000, 847, 2, 1, 1)]
    [InlineData(20, 0, 0, 0, 0)]
    [InlineData(38, 0, 1, 0, 0)]
    public async Task RealRoundAppliesDoubleEchoAndSkillZoneWithoutRepeatingSkillsOrActingAfterDeath(
        int monsterHp, int expectedHp, int echoCount, int doubleCount, int skillCount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var character = new Character { Id = 1, UserId = 1, Name = "测试骑士", ProfessionCode = "knight",
            Attack = 20, Hp = 100, MaxHp = 100, WeaponStaminaPercent = 20,
            WeaponDoubleAttackChancePercent = 100, WeaponNormalEchoPercent = 10,
            WeaponCriticalChancePercent = 100, WeaponSkillDamagePercent = 50 };
        db.AddRange(new User { Id = 1, UserName = "effect-test", PasswordHash = "x", ActiveCharacterId = 1 }, character,
            new UserLoginSession { UserId = 1, Token = "test", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
            new Dungeon { Id = 1, Code = "slime-field", Name = "测试场", MonsterName = "史莱姆", MonsterMaxHp = monsterHp, MonsterAttack = 8, SlotCount = 5 },
            new Monster { Id = 1, Name = "史莱姆", MaxHp = monsterHp, Hp = monsterHp, Attack = 8, Defense = 0 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true },
            new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
            new CharacterSkillSlot { CharacterId = 1, SlotIndex = 1, SkillCode = "knight-strike", AutoUseEnabled = true });
        await db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var battle = new BattleService(db, new UserService(db, progression, skills),
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression));

        var (result, error) = await battle.StartPreparationAsync(1, "test");

        Assert.Null(error);
        Assert.Equal(expectedHp, result!.MonsterHp);
        Assert.Equal(echoCount, result.Logs.Count(log => log.Contains("普攻追击")));
        Assert.Equal(doubleCount, result.Logs.Count(log => log.Contains("二连击")));
        Assert.Equal(skillCount, result.Logs.Count(log => log.Contains("使用 盾击")));
        Assert.Equal(skillCount, await db.BattleSkillCooldowns.CountAsync());
        Assert.Equal(1, (await db.Rooms.SingleAsync()).RoundNumber);
        if (skillCount > 0) Assert.Equal(3, (await db.BattleSkillCooldowns.SingleAsync()).ReadyAtRound);
    }

    [Fact]
    public async Task MigrationPreservesEquipmentQualityAndRefreshesPersistedEffectsWithoutHealing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260921110000_AddRegions");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Users (Id,UserName,PasswordHash,ActiveCharacterId,Gold,Version) VALUES (1,'legacy','x',1,0,0);
            INSERT INTO Characters (Id,UserId,Name,ProfessionCode,Hp,MaxHp,Attack,Defense,
                WeaponAttackBonusPercent,WeaponHealthBonusPercent,WeaponCriticalChancePercent,
                Level,Experience,TalentPoints,AttackTalentRank,DefenseTalentRank,HealthTalentRank,Version)
            VALUES (1,1,'旧角色','knight',25,50,20,5,0,0,0,1,0,0,0,0,0,0);
            INSERT INTO CharacterWeapons (Id,CharacterId,WeaponCode,Name,Element,Attack,MaxHp,
                ItemLevel,SellGold,DismantleFragments,IsLocked,EquippedSlotIndex,Version)
            VALUES (1,1,'custom','旧武器','Fire',20,50,1,10,1,1,1,0);
            INSERT INTO CharacterWeaponSkills (WeaponId,SlotIndex,SkillCode,Level,BaseLevel,QualityBonusLevel,EnhancementLevel)
            VALUES (1,1,'weapon-stamina',6,2,3,1);
            """);
        await DbInitializer.InitializeAsync(db, ProductionCatalog());
        var character = await db.Characters.SingleAsync();
        var weapon = await db.CharacterWeapons.Include(item => item.Skills).SingleAsync();
        Assert.Equal(12, character.WeaponStaminaPercent);
        Assert.Equal(25, character.Hp);
        Assert.Equal(3, weapon.Skills.Single().QualityBonusLevel);
        Assert.Equal(1, weapon.Skills.Single().EnhancementLevel);
        Assert.Equal(2, weapon.Skills.Single().SpentFragments);
        Assert.True(weapon.IsLocked);
        Assert.Equal(1, weapon.EquippedSlotIndex);
        var version = character.Version;
        await DbInitializer.InitializeAsync(db, ProductionCatalog());
        Assert.Equal(version, character.Version);
    }
}
