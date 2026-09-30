using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WeaponTierBalanceTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        TestRepository.File("Game.Server", "appsettings.json"))).Build();

    [Theory]
    [InlineData("durotar-valley-boar", "t1-candle-staff", 125, 95, "weapon-critical")]
    [InlineData("dun-morogh-snow-hare", "t1-ice-tusk-mallet", 115, 110, "weapon-double")]
    [InlineData("northshire-wolves", "t1-stone-edge-hatchet", 100, 120, "weapon-health")]
    [InlineData("mulgore-plainstrider-chick", "t1-feather-short-staff", 120, 105, "weapon-stamina")]
    [InlineData("eversong-golden-lynx", "t1-sentry-old-sword", 110, 115, "weapon-resilience")]
    [InlineData("tirisfal-dusk-bat", "t1-dim-apprentice-staff", 130, 90, "weapon-enmity")]
    public void FirstHuntPrimaryWeaponStartsWithAttackAndUnlocksSecondSkillAtRankThree(
        string hunt, string code, int attack, int maxHp, string secondCode)
    {
        var rewards = Configuration().GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        Assert.Equal(code, rewards.MonsterKills[hunt].Drops.First(drop => drop.Kind == "Weapon").Code);
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var template = catalog.FindItem(code)!;
        Assert.Equal((attack, maxHp), (template.Attack, template.MaxHp));
        Assert.Equal(new[] { "weapon-attack", secondCode }, template.Skills.Select(skill => skill.Code));
        Assert.Equal(new[] { 0, 3 }, template.Skills.Select(skill => skill.UnlockQualityRank));
        var snapshot = catalog.CreateRewardSnapshot(code);
        Assert.Equal(0, snapshot.QualityRank);
        Assert.Equal("weapon-attack", Assert.Single(snapshot.Skills).Code);
        var config = Configuration();
        var rewardsCatalog = new RewardCatalog(
            Options.Create(config.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!),
            new ConsumableCatalog(Options.Create(config.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!)),
            catalog,
            new MaterialCatalog(Options.Create(config.GetSection(MaterialOptions.SectionName).Get<MaterialOptions>()!)),
            new SoulImprintCatalog(Options.Create(config.GetSection(SoulImprintOptions.SectionName).Get<SoulImprintOptions>()!)));
        var guaranteed = rewardsCatalog.FirstHuntWeapon(hunt)!;
        Assert.Equal((code, 0), (guaranteed.Code, guaranteed.QualityRank));
        Assert.Equal("weapon-attack", Assert.Single(guaranteed.Skills).Code);
        var preview = rewardsCatalog.GetDropPreview(hunt, false).First(drop => drop.Kind == "Weapon" && drop.Code == code);
        Assert.Equal(new[] { 0, 3 }, preview.Weapon!.Skills.Select(skill => skill.UnlockQualityRank));
        var weapon = snapshot.ToCharacterWeapon(1);
        Assert.Equal("weapon-attack", Assert.Single(weapon.Skills).SkillCode);
        for (var rank = 1; rank <= 3; rank++)
        {
            weapon.QualityRank = rank;
            catalog.UnlockSkillsForQuality(weapon);
            Assert.Equal(rank == 3 ? 2 : 1, weapon.Skills.Count);
        }
        var unlocked = weapon.Skills.Single(skill => skill.SkillCode == secondCode);
        Assert.Equal((1, 1, 0, 2), (unlocked.Level, unlocked.BaseLevel, unlocked.EnhancementLevel, unlocked.SlotIndex));
        catalog.UnlockSkillsForQuality(weapon);
        Assert.Equal(2, weapon.Skills.Count);
    }

    [Fact]
    public void AllTiersUseConfiguredPerLevelRatesAndShareEffectCaps()
    {
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var specs = new (string Code, decimal Small, decimal Medium, decimal Large)[]
        {
            ("attack", 1.5m, 2.5m, 4m), ("health", 2m, 3m, 4.5m),
            ("critical", 1m, 1.5m, 2m), ("double", 1.25m, 1.75m, 2.5m),
            ("echo", 1m, 1.5m, 2m), ("stamina", 1m, 2m, 3m),
            ("enmity", 2.5m, 5m, 7.5m), ("resilience", .5m, 1m, 1.5m),
            ("skill", 1m, 2m, 3m), ("growth", .25m, .4m, .6m),
            ("resolve", 1.25m, 2.5m, 3.75m), ("bane", 1.25m, 2m, 2.5m)
        };
        foreach (var (code, small, medium, large) in specs)
        {
            Assert.Equal(small * 10, catalog.CalculateSkillPercent(catalog.FindSkill($"weapon-{code}-small")!, 10));
            Assert.Equal(medium * 10, catalog.CalculateSkillPercent(catalog.FindSkill($"weapon-{code}")!, 10));
            Assert.Equal(large * 10, catalog.CalculateSkillPercent(catalog.FindSkill($"weapon-{code}-large")!, 10));
        }
        var weapon = new CharacterWeapon { Element = ElementType.Fire, EquippedSlotIndex = 1,
            Skills = [new CharacterWeaponSkill { SkillCode = "weapon-attack-small", Level = 10 },
                new CharacterWeaponSkill { SkillCode = "weapon-attack-large", Level = 10 },
                new CharacterWeaponSkill { SkillCode = "weapon-might", Level = 10 }] };
        Assert.Equal(70m, catalog.CalculateBonuses([weapon]).AttackPercent);
        var defenseWeapon = new CharacterWeapon { Element = ElementType.Fire, EquippedSlotIndex = 1,
            Skills = [new CharacterWeaponSkill { SkillCode = "weapon-resilience-large", Level = 10 },
                new CharacterWeaponSkill { SkillCode = "weapon-resolve-large", Level = 10 }] };
        var defense = catalog.CalculateBonuses([defenseWeapon]);
        Assert.Equal(15m, defense.Percent(WeaponSkillEffectType.DirectDamageReductionPercent));
        Assert.Equal(35m, defense.Percent(WeaponSkillEffectType.LowHpDamageReductionPercent));
    }

    [Fact]
    public void RampAdvantageAndDirectReductionRespectRoundThresholdAndSharedCaps()
    {
        var character = new Character { MaxHp = 100, Hp = 100, WeaponAttackBonusPercent = 10,
            CombatWeaponRampAttackPerRoundPercent = 4, CombatWeaponElementAdvantagePercent = 100,
            CombatWeaponDirectReductionPercent = 20, CombatWeaponLowHpReductionPercent = 40 };
        Assert.Equal(14, WeaponCombatRules.AttackBonusPercent(character, 0));
        Assert.Equal(50, WeaponCombatRules.AttackBonusPercent(character, 9));
        Assert.Equal(50, WeaponCombatRules.AttackBonusPercent(character, 50));
        character.WeaponAttackBonusPercent = 280;
        Assert.Equal(300, WeaponCombatRules.AttackBonusPercent(character, 9));
        Assert.Equal(50, WeaponCombatRules.ElementAttackPercent(ElementType.Fire, ElementType.Wind, 100));
        Assert.Equal(-25, WeaponCombatRules.ElementAttackPercent(ElementType.Fire, ElementType.Water, 100));
        Assert.Equal(0, WeaponCombatRules.ElementAttackPercent(ElementType.Fire, ElementType.Earth, 100));
        Assert.Equal(20, WeaponCombatRules.WeaponReductionPercent(character));
        character.Hp = 25;
        Assert.Equal(40, WeaponCombatRules.WeaponReductionPercent(character));
        character.Hp = 1;
        Assert.Equal(50, WeaponCombatRules.WeaponReductionPercent(character));
        Assert.Equal(75, WeaponCombatRules.CombinedDirectReductionPercent(50, character));
        Assert.Equal(90, WeaponCombatRules.CombinedDirectReductionPercent(80, character));
    }

    [Fact]
    public async Task ThirdBreakthroughUnlocksSecondSkillAndImmediatelyUpdatesEquippedBonuses()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var weapon = catalog.CreateRewardSnapshot("t1-candle-staff").ToCharacterWeapon(1);
        weapon.EquippedSlotIndex = 1;
        var character = new Character { Id = 1, UserId = 1, Name = "test", Attack = weapon.Attack,
            MaxHp = weapon.MaxHp, Hp = weapon.MaxHp, WeaponAttackBonusPercent = 2.5m };
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow.AddDays(1) }, character, weapon,
            new CharacterItemStack { CharacterId = 1, ItemCode = "weapon-breakthrough-stone-t1", Quantity = 3 });
        await db.SaveChangesAsync();
        var options = Configuration().GetSection(WeaponBreakthroughOptions.SectionName).Get<WeaponBreakthroughOptions>()!;
        var skills = SkillTestFactory.Create();
        var service = new WeaponService(db, new UserService(db, ProgressionTestFactory.Create(), skills), skills,
            catalog, new WeaponBreakthroughCatalog(Options.Create(options)));
        for (var rank = 1; rank <= 3; rank++)
        {
            var (response, error) = await service.UpgradeQualityAsync("token", 1, weapon.Id,
                new UpgradeWeaponQualityRequest { UseUniversalStone = true });
            Assert.Null(error);
            var equipped = response!.Weapons.Single(item => item.Id == weapon.Id);
            Assert.Equal(rank == 3 ? 2 : 1, equipped.Skills.Count);
            Assert.Equal(rank == 3 ? 1.5m : 0m, response.CriticalChancePercent);
        }
        Assert.Equal(1.5m, character.WeaponCriticalChancePercent);
        Assert.Equal(0, (await db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task RealRoundUsesRampBaneAndWeaponReductionFromEquippedSkills()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        var character = new Character { Id = 1, UserId = 1, Name = "test", ProfessionCode = "knight",
            Attack = 100, MaxHp = 1000, Hp = 1000, WeaponAttackBonusPercent = 2.5m };
        var weapon = new CharacterWeapon { CharacterId = 1, WeaponCode = "test-combat", Name = "Test",
            Element = ElementType.Fire, Attack = 100, MaxHp = 1000, EquippedSlotIndex = 1,
            Skills = [new CharacterWeaponSkill { SlotIndex = 1, SkillCode = "weapon-attack", BaseLevel = 1, Level = 1 },
                new CharacterWeaponSkill { SlotIndex = 2, SkillCode = "weapon-growth", BaseLevel = 10, Level = 10 },
                new CharacterWeaponSkill { SlotIndex = 3, SkillCode = "weapon-bane", BaseLevel = 10, Level = 10 }] };
        var defenseWeapon = new CharacterWeapon { CharacterId = 1, WeaponCode = "test-defense", Name = "Defense",
            Element = ElementType.Fire, Attack = 0, MaxHp = 1, EquippedSlotIndex = 2,
            Skills = [new CharacterWeaponSkill { SlotIndex = 1, SkillCode = "weapon-resilience", BaseLevel = 10, Level = 10 }] };
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow.AddDays(1) }, character, weapon, defenseWeapon,
            new Dungeon { Id = 1, Code = "test-field", Name = "Test", MonsterName = "Wind", MonsterMaxHp = 1000,
                MonsterAttack = 100, SlotCount = 5 },
            new Monster { Id = 1, Name = "Wind", Element = ElementType.Wind, Hp = 1000,
                MaxHp = 1000, Attack = 100 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1,
                IsOwnerAutoEnabled = true, SlotCount = 5, Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsAutoEnabled = true },
            new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
            new CharacterBattleMilestone { CharacterId = 1, Kind = BattleMilestoneService.DungeonClearKind,
                TargetCode = "test-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var battle = new BattleService(db, new UserService(db, progression, skills),
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression), weaponCatalog: catalog);
        var (result, error) = await battle.StartPreparationAsync(1, "token");
        Assert.Null(error);
        // 100 attack × (1 + 2.5% attack + 4% first-round ramp) × 1.30 Fire-vs-Wind advantage.
        Assert.Equal(862, result!.MonsterHp);
        // Wind is weak against Fire; 100 × 0.75 × (1 - 10% weapon reduction).
        Assert.Equal(933, character.Hp);
        Assert.Equal(1, (await db.Rooms.SingleAsync()).RoundNumber);
    }
}
