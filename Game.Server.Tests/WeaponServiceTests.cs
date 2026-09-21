using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WeaponServiceTests
{
    [Fact]
    public void ProductionWeaponSkillConfigurationLoads()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"));
        var options = new WeaponOptions();
        new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection(WeaponOptions.SectionName).Bind(options);

        var catalog = new WeaponCatalog(Options.Create(options));
        var starters = catalog.CreateStarterWeapons(1, "knight");
        var bonuses = catalog.CalculateBonuses(starters);

        Assert.Equal(4, starters.Count);
        Assert.Contains(starters, weapon => weapon.WeaponCode == "cinder-knife" && weapon.EquippedSlotIndex is null);
        Assert.Equal(4, bonuses.AttackPercent);
        Assert.Equal(0, bonuses.HealthPercent);
        Assert.Equal(2, Assert.Single(starters.Single(weapon => weapon.EquippedSlotIndex == 1).Skills).Level);
        starters.Single(weapon => weapon.WeaponCode == "cinder-knife").EquippedSlotIndex = 2;
        Assert.Equal(4, Assert.Single(catalog.CalculateBonuses(starters).ActiveSkills).Level);
    }

    [Fact]
    public async Task MainElementFiltersPassiveSkillsAndHealthBonusUpdatesImmediatelyOnSwap()
    {
        await using var test = await WeaponTestContext.CreateAsync(CreateSkillCatalog());
        var water = test.Weapons.Single(weapon => weapon.WeaponCode == "tide-saber");
        var (initial, _) = await test.Service.GetAsync(test.Token, 1);
        Assert.Equal(4, initial!.AttackBonusPercent);
        Assert.Equal(0, initial.HealthBonusPercent);
        Assert.Equal(2, Assert.Single(initial.ActiveSkills).Level);

        var (offElement, _) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest { WeaponId = water.Id });
        Assert.Equal(4, offElement!.AttackBonusPercent);
        Assert.Equal(0, offElement.HealthBonusPercent);
        Assert.False(offElement.Weapons.Single(weapon => weapon.Id == water.Id).Skills.Single().IsActive);

        var (swapped, error) = await test.Service.SetSlotAsync(test.Token, 1, 1,
            new SetWeaponSlotRequest { WeaponId = water.Id });
        Assert.Null(error);
        Assert.Equal(ElementType.Water, swapped!.MainElement);
        Assert.Equal(0, swapped.AttackBonusPercent);
        Assert.Equal(6, swapped.HealthBonusPercent);
        Assert.Equal(131, swapped.EffectiveMaxHp); // floor((100 + 24) * 1.06)
        Assert.Equal(100, swapped.Hp); // Equipping does not heal.
        Assert.Equal("生命", Assert.Single(swapped.ActiveSkills).Name);
        Assert.False(swapped.Weapons.Single(weapon => weapon.WeaponCode == "ember-blade").Skills.Single().IsActive);
    }

    [Fact]
    public async Task SameElementSkillLevelsAddAcrossWeapons()
    {
        await using var test = await WeaponTestContext.CreateAsync(CreateSkillCatalog());
        var secondFire = new CharacterWeapon
        {
            CharacterId = test.Character.Id, WeaponCode = "test-fire", Name = "Test Fire",
            Element = ElementType.Fire, Attack = 5, MaxHp = 20,
            Skills =
            [
                new CharacterWeaponSkill { SlotIndex = 1, SkillCode = "weapon-attack", Level = 2 },
                new CharacterWeaponSkill { SlotIndex = 2, SkillCode = "weapon-health", Level = 1 }
            ]
        };
        test.Db.CharacterWeapons.Add(secondFire);
        await test.Db.SaveChangesAsync();

        var (equipped, error) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest { WeaponId = secondFire.Id });

        Assert.Null(error);
        Assert.Equal(8, equipped!.AttackBonusPercent);
        Assert.Equal(3, equipped.HealthBonusPercent);
        Assert.Equal(123, equipped.EffectiveMaxHp);
        Assert.Equal(4, equipped.ActiveSkills.Single(skill => skill.SkillCode == "weapon-attack").Level);
        Assert.Equal(1, equipped.ActiveSkills.Single(skill => skill.SkillCode == "weapon-health").Level);
        Assert.Equal(25, equipped.TotalAttack);
    }

    [Fact]
    public async Task CriticalAndAttackSkillsAffectNormalHitsAndDamageSkills()
    {
        await using var test = await WeaponTestContext.CreateAsync(CreateSkillCatalog(guaranteedCritical: true));
        test.Db.AddRange(
            new Dungeon { Id = 1, Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterMaxHp = 200, MonsterAttack = 8, MonsterDefense = 5, SlotCount = 5 },
            new Monster { Id = 1, Name = "Slime", Hp = 200, MaxHp = 200, Attack = 8, Defense = 5 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true },
            new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
            new CharacterSkillSlot { CharacterId = 1, SlotIndex = 1, SkillCode = "knight-strike", AutoUseEnabled = true });
        await test.Db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var battle = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression));

        var (round, error) = await battle.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(100, test.Character.WeaponCriticalChancePercent);
        Assert.Equal(126, round!.MonsterHp); // 29 normal + 45 skill damage.
        Assert.Equal(2, round.Logs.Count(log => log.Contains("(critical)")));
        Assert.Contains(round.Logs, log => log.Contains("uses 盾击") && log.Contains("45 damage"));
    }

    [Fact]
    public async Task EquipSumsAllElementsSwapsMainAndClampsHealthOnUnequip()
    {
        await using var test = await WeaponTestContext.CreateAsync();
        var fire = test.Weapons.Single(weapon => weapon.WeaponCode == "ember-blade");
        var water = test.Weapons.Single(weapon => weapon.WeaponCode == "tide-saber");
        var wind = test.Weapons.Single(weapon => weapon.WeaponCode == "gale-bow");

        var (withWater, waterError) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest { WeaponId = water.Id });
        Assert.Null(waterError);
        Assert.Equal(28, withWater!.TotalAttack);
        Assert.Equal(124, withWater.TotalMaxHp);
        Assert.Equal(ElementType.Fire, withWater.MainElement);
        Assert.Equal(100, withWater.Hp); // Equipping does not heal.

        await test.Service.SetSlotAsync(test.Token, 1, 3, new SetWeaponSlotRequest { WeaponId = wind.Id });
        var (swapped, swapError) = await test.Service.SetSlotAsync(test.Token, 1, 1,
            new SetWeaponSlotRequest { WeaponId = water.Id });
        Assert.Null(swapError);
        Assert.Equal(ElementType.Water, swapped!.MainElement);
        Assert.Equal(35, swapped.TotalAttack);
        Assert.Equal(152, swapped.TotalMaxHp);
        Assert.Equal(2, swapped.Weapons.Single(weapon => weapon.Id == fire.Id).EquippedSlotIndex);
        Assert.Equal(1, swapped.Weapons.Single(weapon => weapon.Id == water.Id).EquippedSlotIndex);

        var (unequipped, unequipError) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest());
        Assert.Null(unequipError);
        Assert.Equal(15, unequipped!.TotalAttack);
        Assert.Equal(52, unequipped.TotalMaxHp);
        Assert.Equal(52, unequipped.Hp);
        Assert.Equal(52, test.Character.Hp);
    }

    [Fact]
    public async Task RejectsForeignWeaponsEmptyMainAndCombatLoadoutChanges()
    {
        await using var test = await WeaponTestContext.CreateAsync();
        var other = new CharacterWeapon
        {
            CharacterId = 2, WeaponCode = "dusk-dagger", Name = "暮影匕首",
            Element = ElementType.Dark, Attack = 14, MaxHp = 18
        };
        test.Db.CharacterWeapons.Add(other);
        await test.Db.SaveChangesAsync();

        var (foreign, foreignError) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest { WeaponId = other.Id });
        var (emptyMain, mainError) = await test.Service.SetSlotAsync(test.Token, 1, 1,
            new SetWeaponSlotRequest());
        Assert.Null(foreign);
        Assert.Equal("WeaponNotOwned", foreignError);
        Assert.Null(emptyMain);
        Assert.Equal("MainWeaponRequired", mainError);

        var (movedMain, movedMainError) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest { WeaponId = test.Weapons.Single(weapon => weapon.EquippedSlotIndex == 1).Id });
        Assert.Null(movedMain);
        Assert.Equal("MainWeaponRequired", movedMainError);

        test.Db.Rooms.Add(new Room { Id = 1, OwnerUserId = 1, Status = RoomStatus.Preparing });
        test.Db.RoomSlots.Add(new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1 });
        await test.Db.SaveChangesAsync();
        var (locked, lockError) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest { WeaponId = test.Weapons.Single(weapon => weapon.WeaponCode == "tide-saber").Id });
        Assert.Null(locked);
        Assert.Equal("LoadoutLocked", lockError);
        Assert.Equal(20, test.Character.Attack);
    }

    [Fact]
    public async Task EquippedWeaponAttackIsUsedByBattleSettlement()
    {
        await using var test = await WeaponTestContext.CreateAsync();
        test.Db.AddRange(
            new Dungeon { Id = 1, Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterMaxHp = 50, MonsterAttack = 8, MonsterDefense = 2, SlotCount = 5 },
            new Monster { Id = 1, Name = "Slime", Hp = 50, MaxHp = 50, Attack = 8, Defense = 2 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true },
            new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow });
        await test.Db.SaveChangesAsync();
        var water = test.Weapons.Single(weapon => weapon.WeaponCode == "tide-saber");
        var (_, equipError) = await test.Service.SetSlotAsync(test.Token, 1, 2,
            new SetWeaponSlotRequest { WeaponId = water.Id });
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var battle = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression));

        var (round, error) = await battle.StartPreparationAsync(1, test.Token);

        Assert.Null(equipError);
        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("Knight attacks Slime for 32 damage"));
        Assert.Equal(18, round.MonsterHp);
        Assert.Equal(98, test.Character.Hp);
    }

    [Fact]
    public async Task NewCharacterReceivesOwnedStarterWeapons()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-weapon-register-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var catalog = CreateSkillCatalog();
            var users = new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create(), catalog);

            var (registration, error) = await users.RegisterAsync(new RegisterRequest
            {
                UserName = "weapon-tester", Password = "test-password"
            });

            Assert.Null(error);
            Assert.NotNull(registration);
            var character = await db.Characters.SingleAsync();
            var owned = await db.CharacterWeapons.Include(weapon => weapon.Skills)
                .Where(weapon => weapon.CharacterId == character.Id).ToListAsync();
            Assert.Equal(3, owned.Count);
            Assert.Equal("ember-blade", owned.Single(weapon => weapon.EquippedSlotIndex == 1).WeaponCode);
            Assert.Equal(20, character.Attack);
            Assert.Equal(100, character.MaxHp);
            Assert.Equal(100, character.Hp);
            Assert.Equal(4, character.WeaponAttackBonusPercent);
            Assert.Equal(("weapon-attack", 2),
                (Assert.Single(owned.Single(weapon => weapon.EquippedSlotIndex == 1).Skills).SkillCode,
                    owned.Single(weapon => weapon.EquippedSlotIndex == 1).Skills[0].Level));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task MigrationPreservesBaseStatsAndAddsWeaponSkillToExistingMain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-weapon-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.GetService<IMigrator>().MigrateAsync("20260921010000_UnifyTalentTree");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Users (Id, UserName, PasswordHash, ActiveCharacterId) VALUES (1, 'old', 'x', NULL);
                INSERT INTO Characters (Id, UserId, Name, ProfessionCode, Level, Experience, TalentPoints,
                    AttackTalentRank, DefenseTalentRank, HealthTalentRank, Hp, MaxHp, Attack, Defense, Version)
                VALUES (1, 1, 'Old Knight', 'knight', 1, 0, 0, 0, 0, 0, 52, 93, 17, 5, 0);
                """);

            await db.Database.MigrateAsync();

            var weapons = await db.CharacterWeapons.Where(weapon => weapon.CharacterId == 1).ToListAsync();
            var main = Assert.Single(weapons, weapon => weapon.EquippedSlotIndex == 1);
            Assert.Equal(17, main.Attack);
            Assert.Equal(93, main.MaxHp);
            Assert.Equal(ElementType.Fire, main.Element);
            Assert.Equal(17, (await db.Characters.SingleAsync()).Attack);
            Assert.Equal(52, (await db.Characters.SingleAsync()).Hp);
            Assert.Equal(4, weapons.Count);
            Assert.Contains(weapons, weapon => weapon.WeaponCode == "cinder-knife" && weapon.EquippedSlotIndex is null);
            Assert.Equal(4, (await db.Characters.SingleAsync()).WeaponAttackBonusPercent);
            var mainSkill = await db.CharacterWeaponSkills.SingleAsync(skill => skill.WeaponId == main.Id);
            Assert.Equal(("weapon-attack", 2), (mainSkill.SkillCode, mainSkill.Level));

            var adjustedCatalog = CreateSkillCatalog(attackPerLevel: 3);
            await DbInitializer.InitializeAsync(db, adjustedCatalog);
            Assert.Equal(6, (await db.Characters.SingleAsync()).WeaponAttackBonusPercent);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static WeaponCatalog CreateCatalog() => new(Options.Create(new WeaponOptions
    {
        Items =
        [
            new WeaponTemplateOptions { Code = "ember-blade", Name = "余烬长剑", Element = ElementType.Fire, Attack = 20, MaxHp = 100 },
            new WeaponTemplateOptions { Code = "tide-saber", Name = "潮汐弯刀", Element = ElementType.Water, Attack = 8, MaxHp = 24 },
            new WeaponTemplateOptions { Code = "gale-bow", Name = "疾风短弓", Element = ElementType.Wind, Attack = 7, MaxHp = 28 }
        ],
        StarterPacks = new Dictionary<string, List<string>>
        {
            ["knight"] = ["ember-blade", "tide-saber", "gale-bow"]
        }
    }));

    private static WeaponCatalog CreateSkillCatalog(bool guaranteedCritical = false, decimal attackPerLevel = 2) => new(Options.Create(new WeaponOptions
    {
        Skills =
        [
            new WeaponSkillDefinitionOptions { Code = "weapon-attack", Name = "攻击", EffectType = WeaponSkillEffectType.AttackPercent, PercentPerLevel = attackPerLevel },
            new WeaponSkillDefinitionOptions { Code = "weapon-health", Name = "生命", EffectType = WeaponSkillEffectType.MaxHpPercent, PercentPerLevel = 3 },
            new WeaponSkillDefinitionOptions { Code = "weapon-critical", Name = "暴击率", EffectType = WeaponSkillEffectType.CriticalChancePercent, PercentPerLevel = 5 }
        ],
        Items =
        [
            new WeaponTemplateOptions { Code = "ember-blade", Name = "余烬长剑", Element = ElementType.Fire, Attack = 20, MaxHp = 100,
                Skills = guaranteedCritical
                    ? [new WeaponSkillGrantOptions { Code = "weapon-attack", Level = 2 }, new WeaponSkillGrantOptions { Code = "weapon-critical", Level = 20 }]
                    : [new WeaponSkillGrantOptions { Code = "weapon-attack", Level = 2 }] },
            new WeaponTemplateOptions { Code = "tide-saber", Name = "潮汐弯刀", Element = ElementType.Water, Attack = 8, MaxHp = 24,
                Skills = [new WeaponSkillGrantOptions { Code = "weapon-health", Level = 2 }] },
            new WeaponTemplateOptions { Code = "gale-bow", Name = "疾风短弓", Element = ElementType.Wind, Attack = 7, MaxHp = 28,
                Skills = [new WeaponSkillGrantOptions { Code = "weapon-critical", Level = 2 }] }
        ],
        StarterPacks = new Dictionary<string, List<string>>
        {
            ["knight"] = ["ember-blade", "tide-saber", "gale-bow"]
        }
    }));

    private sealed class WeaponTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private WeaponTestContext(string path, GameDbContext db, Character character, List<CharacterWeapon> weapons, WeaponCatalog catalog)
        {
            _path = path;
            Db = db;
            Character = character;
            Weapons = weapons;
            var skills = SkillTestFactory.Create();
            Service = new WeaponService(db, new UserService(db, ProgressionTestFactory.Create(), skills), skills, catalog);
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Character Character { get; }
        public List<CharacterWeapon> Weapons { get; }
        public WeaponService Service { get; }

        public static async Task<WeaponTestContext> CreateAsync(WeaponCatalog? configuredCatalog = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-weapon-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20, Defense = 5 };
            var catalog = configuredCatalog ?? CreateCatalog();
            var weapons = catalog.CreateStarterWeapons(1, "knight").ToList();
            catalog.ApplyBonuses(character, weapons);
            character.Hp = TalentRules.EffectiveMaxHp(character);
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 },
                character, new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            db.CharacterWeapons.AddRange(weapons);
            await db.SaveChangesAsync();
            return new WeaponTestContext(path, db, character, weapons, catalog);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }
}
