using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterStatSnapshotTests
{
    [Fact]
    public void EquipmentRecalculationUsesOnlyEquippedWeaponsAndDoesNotHealOrAdvanceVersion()
    {
        var options = new WeaponOptions();
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"));
        new ConfigurationBuilder().AddJsonFile(path).Build().GetSection(WeaponOptions.SectionName).Bind(options);
        var catalog = new WeaponCatalog(Options.Create(options));
        var weapons = catalog.CreateStarterWeapons(1, "knight").ToList();
        weapons.Add(new() { Attack = 999999, MaxHp = 999999 });
        var character = new Character { Attack = 99, MaxHp = 99, Hp = 17, Version = 4 };

        var stats = catalog.RecalculateEquipmentStats(character, weapons);

        Assert.Equal(weapons.Where(weapon => weapon.EquippedSlotIndex.HasValue).Sum(weapon => weapon.Attack), character.Attack);
        Assert.Equal(weapons.Where(weapon => weapon.EquippedSlotIndex.HasValue).Sum(weapon => weapon.MaxHp), character.MaxHp);
        Assert.Equal(20m, stats.Bonuses.HealthPercent);
        Assert.Equal(20m, character.WeaponHealthBonusPercent);
        Assert.Equal(17, character.Hp);
        Assert.Equal(4, character.Version);
    }

    [Fact]
    public void TemporaryCalculationIsPureAndNullResetDoesNotOverwritePersistentBonuses()
    {
        var character = new Character { WeaponAttackBonusPercent = 25, WeaponHealthBonusPercent = 20,
            WeaponSkillDamagePercent = 10, TemporaryWeaponAttackBonusPercent = 77 };
        var bonuses = new WeaponSkillBonuses(40, 35, 8, [],
            [new(WeaponSkillEffectType.SkillDamagePercent, 1, 22), new(WeaponSkillEffectType.DirectDamageReductionPercent, 1, 15)]);

        var temporary = BattleConsumableBonusCalculator.Calculate(character, bonuses);

        Assert.Equal(77m, character.TemporaryWeaponAttackBonusPercent);
        Assert.Equal(15m, temporary.AttackPercent);
        Assert.Equal(15m, temporary.HealthPercent);
        Assert.Equal(12m, temporary.SkillDamagePercent);
        temporary.ApplyTo(character);
        Assert.Equal(40m, CharacterCombatStatSnapshot.Capture(character).AttackPercent);
        Assert.Equal(15m, character.CombatWeaponDirectReductionPercent);
        BattleConsumableBonusCalculator.Apply(character, null);
        Assert.Equal(25m, character.WeaponAttackBonusPercent);
        Assert.Equal(20m, character.WeaponHealthBonusPercent);
        Assert.Equal(0m, character.TemporaryWeaponAttackBonusPercent);
        Assert.Equal(0m, character.CombatWeaponDirectReductionPercent);
    }

    [Fact]
    public void ExecutionFreezesStatsWhileHpRemainsLiveAndNextExecutionRefreshesBonuses()
    {
        var character = new Character { Id = 1, Attack = 20, MaxHp = 101, Hp = 130,
            WeaponHealthBonusPercent = 17.5m, TemporaryWeaponHealthBonusPercent = 7.5m,
            TalentMaxHpPercent = 12.5m, WeaponAttackBonusPercent = 20 };
        var battle = Context(character);
        var actor = Assert.Single(battle.Characters);
        // floor(101 * 1.25 * 1.125), rather than flooring each multiplier separately.
        Assert.Equal(142, actor.MaxHp);
        character.TemporaryWeaponHealthBonusPercent = 0;
        character.Attack = 999;
        Assert.Equal(20, battle.StatsFor(character).Attack);
        Assert.Equal(142, actor.MaxHp);
        Assert.Equal(12, BattleDamageService.RestoreHp(actor, 99));
        Assert.Equal(142, character.Hp);
        character.Hp = 30;
        Assert.Equal(30, actor.Hp);
        var next = Context(character);
        Assert.Equal(999, next.StatsFor(character).Attack);
        Assert.Equal(133, next.StatsFor(character).MaxHp);
    }

    [Fact]
    public void RampAndLowHpReductionRetainBoundsAndUseCurrentHp()
    {
        var character = new Character { Id = 1, MaxHp = 100, Hp = 50,
            WeaponAttackBonusPercent = 250, CombatWeaponRampAttackPerRoundPercent = 10,
            CombatWeaponDirectReductionPercent = 20, CombatWeaponLowHpReductionPercent = 30 };
        var stats = Context(character).StatsFor(character);
        Assert.Equal(260m, WeaponCombatRules.AttackBonusPercent(stats, -1));
        Assert.Equal(300m, WeaponCombatRules.AttackBonusPercent(stats, 100));
        Assert.Equal(20m, WeaponCombatRules.WeaponReductionPercent(stats, 50));
        Assert.Equal(35m, WeaponCombatRules.WeaponReductionPercent(stats, 25));
        Assert.Equal(50m, WeaponCombatRules.WeaponReductionPercent(stats, 0));
        Assert.Equal(61m, WeaponCombatRules.CombinedDirectReductionPercent(40, stats, 25));
        Assert.Equal(BattleRules.MaxTotalDamageReductionPercent,
            WeaponCombatRules.CombinedDirectReductionPercent(100, stats, 25));
    }

    [Fact]
    public async Task DirectDamageAndDamageOverTimeUseTheSameFrozenEquipmentInputs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var character = new Character { Id = 1, Attack = 100, MaxHp = 100, Hp = 100, WeaponAttackBonusPercent = 50 };
        var battle = Context(character);
        battle.Monster.Id = 2;
        battle.Monster.Hp = battle.Monster.MaxHp = 1000;
        var source = Assert.Single(battle.Characters);
        var skills = SkillTestFactory.Create();
        var statuses = new BattleStatusService(db, MonsterCombatTestFactory.CreateCatalog().Statuses);
        var guards = new BattleGuardService(statuses);
        var damage = new BattleDamageService(statuses, guards);
        var effects = new BattleEffectExecutor(skills, statuses, guards, damage);
        // Later mutation cannot mix new equipment percentages with an old base attack.
        character.Attack = 500;
        character.WeaponAttackBonusPercent = 300;
        Assert.Equal(150, (await damage.CharacterDamageAsync(battle, source, BattleSkillEffect.Damage(100),
            BattleDamageOrigin.NormalAttack, false)).CalculatedAmount);
        var cast = new BattleCastExecution(battle, skills.FindDefinition("knight-strike")!, source);
        await effects.ApplyStatusAsync(cast,
            new BattleSkillEffect(BattleEffectKind.ApplyStatus, BattleSkillEffect.Damage(100).TargetPolicy,
                AttackPowerPercent: 50, StatusCode: "poison", DurationRounds: 2), battle.Enemy);
        Assert.Equal(75, Assert.Single(db.BattleStatusEffects.Local).PerTickValue);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void MaxHpSnapshotRetainsFloorAndIntegerSaturation(int baseHp, int expected)
    {
        var character = new Character { MaxHp = baseHp, WeaponHealthBonusPercent = 300 };
        Assert.Equal(expected, CharacterCombatStatSnapshot.Capture(character).MaxHp);
    }

    private static BattleExecutionContext Context(Character character) => new(new(), new(),
        [new(new() { SlotIndex = 1, CharacterId = character.Id }, character)],
        new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);
}
