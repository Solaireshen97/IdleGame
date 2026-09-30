using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task MonsterOrderedEffectsCanCleanseHealGuardDamageDispelAndApplyTheirOwnBuff()
    {
        await using var test = await BattleTestContext.CreateAsync(characterDefense: 0, monsterAttack: 20);
        var ally = await test.AddSlotAsync(2, "Ally");
        test.Monster.Hp = 40;
        test.Monster.MaxHp = 100;
        test.Monster.CombatProfileCode = "boss";
        await test.Db.SaveChangesAsync();
        var options = CreateUnifiedMonsterOptions(
        [
            new() { Type = "Cleanse", Target = "Self" },
            new() { Type = "Heal", Target = "Self", HealMaxHpPercent = 20 },
            new() { Type = "Guard", Target = "Self", Power = 50 },
            new() { Type = "Damage", Target = "AllAlive", AttackPowerPercent = 10 },
            new() { Type = "Dispel", Target = "AllAlive" },
            new() { Type = "ApplyStatus", Target = "Self", StatusCode = "boss-strength", DurationRounds = 2 }
        ]);
        options.StatusEffects =
        [
            new() { Code = "weakness", Name = "Weakness", Description = "Reduced attack", EffectType = "AttackPercent", ValuePerStack = -20 },
            new() { Code = "blessing", Name = "Blessing", Description = "Ally buff", EffectType = "AttackPercent", ValuePerStack = 10, IsPositive = true },
            new() { Code = "boss-strength", Name = "Strength", Description = "Boss buff", EffectType = "AttackPercent", ValuePerStack = 10, IsPositive = true }
        ];
        var catalog = new MonsterCombatCatalog(Options.Create(options));
        var service = new MonsterCombatService(test.Db, catalog);
        await service.Statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "weakness", 2, [], "Boss");
        await service.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "blessing", 2, [], "Player");
        var party = await UnifiedPartyAsync(test.Db);
        var logs = new List<string>();

        await service.ExecuteIntentAsync(test.Room, test.Monster, party, new Dictionary<int, ElementType>(), logs);
        await test.Db.SaveChangesAsync();

        Assert.Equal(60, test.Monster.Hp);
        Assert.Equal(98, test.Character.Hp);
        Assert.Equal(98, ally.Hp);
        Assert.False(await service.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "weakness"));
        Assert.False(await service.Statuses.HasAsync(test.Room, "Character", test.Character.Id, "blessing"));
        Assert.Equal(50, (await new BattleGuardService(service.Statuses).DefenseAsync(test.Room, "Monster", test.Monster.Id)).ReductionPercent);
        var buff = Assert.Single(await service.Statuses.GetActiveAsync(test.Room, "Monster", [test.Monster.Id]), state => state.EffectCode == "boss-strength");
        Assert.Equal("Monster", buff.SourceActorType);
        Assert.Equal(test.Monster.Id, buff.SourceActorId);
        Assert.Equal("boss-combination", buff.SourceSkillCode);
        Assert.Equal(3, (await test.Db.BattleMonsterSkillCooldowns.SingleAsync()).ReadyAtRound);
        var removals = logs.FindIndex(log => log.Contains("Weakness"));
        var healing = logs.FindIndex(log => log.Contains("恢复 20"));
        var guarding = logs.FindIndex(log => log.Contains("守护"));
        var hitting = logs.FindIndex(log => log.Contains("攻击 1号位"));
        var dispelling = logs.FindIndex(log => log.Contains("驱散"));
        var buffing = logs.FindIndex(log => log.Contains("获得 Strength"));
        Assert.True(removals < healing && healing < guarding && guarding < hitting && hitting < dispelling && dispelling < buffing);
    }

    [Fact]
    public async Task OrderedResultDistinguishesCalculatedDamageFromActualDamageAndSkipsDeadTargets()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterDefense: 0);
        test.Monster.Hp = 7;
        var monsterCatalog = MonsterCombatTestFactory.CreateCatalog();
        var skills = new SkillCatalog(Options.Create(new SkillOptions
        {
            Professions = [new() { Code = "test", Name = "Test", StartingSkills = ["combo"] }],
            Abilities = [new() { Code = "combo", Name = "Combo", Description = "Hit then guard", ProfessionCode = "test",
                Effects = [new() { Type = "Damage", Target = "Monster" }, new() { Type = "ApplyStatus", Target = "Monster", StatusCode = "poison", DurationRounds = 2 },
                    new() { Type = "Guard", Target = "Self", Power = 25 }] }]
        }), monsterCatalog);
        test.Character.ProfessionCode = "test";
        var statuses = new BattleStatusService(test.Db, monsterCatalog.Statuses);
        var executor = UnifiedExecutor(statuses, skills);
        var battle = new BattleExecutionContext(test.Room, test.Monster, await UnifiedPartyAsync(test.Db),
            new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);

        var result = await executor.ExecuteAsync(new(battle, skills.Resolve(test.Character, "combo")!, battle.Characters[0]));

        Assert.Equal([BattleEffectKind.Damage, BattleEffectKind.Guard], result.Outcomes.Select(effect => effect.Kind).ToArray());
        Assert.Equal(100, result.Damage);
        Assert.Equal(7, result.ActualDamage);
        Assert.Equal(0, test.Monster.Hp);
        Assert.False(await statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "poison"));
        Assert.Equal("combo", Assert.Single(await statuses.GetActiveAsync(test.Room, "Character", [test.Character.Id])).SourceSkillCode);
    }

    [Fact]
    public async Task MageEchoOncePerRoundSurvivesServiceRecreationAndUsesAVisibleStatus()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var (_, combat) = CreateProductionSoulBattleService(test);
        await combat.Statuses.SetCounterAsync(test.Room, "Character", test.Character.Id, "mage-disorder", 6,
            new("Character", test.Character.Id, "mage-arcane-bolt"), "Monster", test.Monster.Id);
        var battle = new BattleExecutionContext(test.Room, test.Monster, await UnifiedPartyAsync(test.Db),
            new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);
        await MageMechanics.RoundStartAsync(battle, UnifiedExecutor(combat.Statuses));
        await test.Db.SaveChangesAsync();
        var flag = Assert.Single(await combat.Statuses.DescribeAsync(test.Room, "Character", test.Character.Id), state => state.Code == "mage-echo-used");
        Assert.Equal("本回合有效", flag.DurationText);
        Assert.Equal(string.Empty, flag.CounterText);
        Assert.Equal(3, await MageMechanics.DisorderStacksAsync(combat.Statuses, test.Room, test.Character.Id, test.Monster.Id));

        await using var fresh = test.CreateDbContext();
        var statuses = new BattleStatusService(fresh, combat.Statuses.Catalog);
        var restored = new BattleExecutionContext(await fresh.Rooms.SingleAsync(), await fresh.Monsters.SingleAsync(),
            await UnifiedPartyAsync(fresh), new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);
        var executor = UnifiedExecutor(statuses);
        await MageMechanics.RoundStartAsync(restored, executor);
        Assert.Empty(restored.Logs);
        Assert.Equal(970, restored.Monster.Hp);

        await statuses.ResolveEndOfRoundAsync(restored.Room, restored.Monster, restored.Party, [], healingOnly: true);
        restored.Room.RoundNumber++;
        await MageMechanics.RoundStartAsync(restored, executor);
        Assert.Single(restored.Logs, log => log.Contains("失序回响"));
        Assert.Equal(940, restored.Monster.Hp);
        Assert.Equal(0, await MageMechanics.DisorderStacksAsync(statuses, restored.Room, test.Character.Id, test.Monster.Id));
    }

    [Fact]
    public async Task StatusMechanicsUseExplicitPowersLevelsAndChargesWithArbitraryCodes()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var charge = new BattleStatusOptions { Code = "reserve", Name = "Reserve", Description = "Three uses", EffectType = "None",
            IsPositive = true, InitialStacks = 3, MaxStacks = 3, CounterKind = BattleStatusCounterKind.Charges,
            Mechanic = BattleStatusMechanic.HunterEagleEye };
        var low = new BattleStatusOptions { Code = "gentle", Name = "Gentle", Description = "Reduction", EffectType = "None",
            Mechanic = BattleStatusMechanic.NextDamageSkillReduction, MechanicPower = 13, FamilyCode = "disruption",
            FamilyRefresh = BattleStatusFamilyRefresh.KeepStronger, Lifetime = BattleStatusLifetime.UntilConsumed };
        var options = new MonsterCombatOptions { StatusEffects =
        [
            charge, low,
            new() { Code = "firm", Name = "Firm", Description = "Reduction", EffectType = "None",
                Mechanic = BattleStatusMechanic.NextDamageSkillReduction, MechanicPower = 24, FamilyCode = "disruption",
                FamilyRefresh = BattleStatusFamilyRefresh.KeepStronger, Lifetime = BattleStatusLifetime.UntilConsumed },
            new() { Code = "realm", Name = "Realm", Description = "Domain rank", EffectType = "None", Mechanic = BattleStatusMechanic.MageDomain, MechanicLevel = 2 },
            new() { Code = "open", Name = "Open", Description = "Vulnerability", EffectType = "None", Mechanic = BattleStatusMechanic.HunterVulnerability, MechanicPower = 12 }
        ] };
        var catalog = new BattleStatusCatalog(Options.Create(options));
        charge.InitialStacks = 1;
        low.MechanicPower = 99;
        var statuses = new BattleStatusService(test.Db, catalog);
        await statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "firm", 0, [], "Enemy", source: new("Character", 11), magnitudeSnapshot: 37);
        await statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "gentle", 0, [], "Enemy", source: new("Character", 12));
        var retained = Assert.Single(await statuses.GetActiveAsync(test.Room, "Monster", [test.Monster.Id]));
        Assert.Equal("firm", retained.EffectCode);
        Assert.Equal(11, retained.SourceActorId);
        Assert.Equal(37, await statuses.ConsumeMechanicPowerAsync(test.Room, "Monster", test.Monster.Id, BattleStatusMechanic.NextDamageSkillReduction));
        Assert.Empty(await statuses.GetActiveAsync(test.Room, "Monster", [test.Monster.Id]));
        await statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "reserve", 2, [], "Player");
        Assert.Equal(3, await statuses.StacksAsync(test.Room, "Character", test.Character.Id, "reserve"));
        await statuses.ConsumeAsync(test.Room, "Character", test.Character.Id, "reserve", 1);
        await statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "reserve", 2, [], "Player");
        Assert.Equal(3, await statuses.StacksAsync(test.Room, "Character", test.Character.Id, "reserve"));
        await statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "realm", 2, [], "Player");
        Assert.Equal(2, await MageMechanics.DomainRankAsync(statuses, test.Room, test.Character.Id));
        await statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "open", 2, [], "Enemy");
        Assert.Equal(112, await statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
    }

    [Fact]
    public async Task MonsterInitialCooldownAndFlatDamageWorkThroughConfiguredEffects()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 10, characterDefense: 0);
        test.Monster.CombatProfileCode = "boss";
        var options = CreateUnifiedMonsterOptions([new() { Type = "Damage", Target = "AllAlive", Power = 7, AttackPowerPercent = 0 }]);
        options.Skills[0].InitialCooldownRounds = 2;
        var service = new MonsterCombatService(test.Db, new MonsterCombatCatalog(Options.Create(options)));
        Assert.Equal("BasicAttack", (await service.EnsureIntentAsync(test.Room, test.Monster)).ActionType);
        test.Room.RoundNumber = 2;
        var intent = await service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal("Skill", intent.ActionType);
        var hp = test.Character.Hp;

        await service.ExecuteIntentAsync(test.Room, test.Monster, await UnifiedPartyAsync(test.Db), new Dictionary<int, ElementType>(), []);

        Assert.Equal(7, hp - test.Character.Hp);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("mixed")]
    [InlineData("unknown-status")]
    [InlineData("mismatched-intent")]
    [InlineData("self-damage")]
    [InlineData("unsupported-interrupt")]
    [InlineData("empty-heal")]
    [InlineData("negative-initial-cooldown")]
    public void InvalidMonsterEffectConfigurationsFailAtStartup(string fault)
    {
        var options = CreateUnifiedMonsterOptions([new() { Type = "Damage", Target = "AllAlive" }]);
        var skill = options.Skills[0];
        switch (fault)
        {
            case "empty": skill.Effects = []; break;
            case "mixed": skill.DamagePowerPercent = 100; break;
            case "unknown-status": skill.Effects = [new() { Type = "ApplyStatus", Target = "Self", StatusCode = "missing", DurationRounds = 2 }]; break;
            case "mismatched-intent": skill.Effects![0].Target = "Front"; break;
            case "self-damage": skill.Effects![0].Target = "Self"; break;
            case "unsupported-interrupt": skill.Effects![0].Type = "Interrupt"; break;
            case "empty-heal": skill.Effects = [new() { Type = "Heal", Target = "Self" }]; break;
            case "negative-initial-cooldown": skill.InitialCooldownRounds = -1; break;
        }
        Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
    }

    [Fact]
    public async Task MonsterPeriodicEffectsSnapshotTheirAttackAndHealTheirOwnHpStartingNextRound()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 20);
        test.Monster.CombatProfileCode = "boss";
        test.Monster.Hp = 40;
        test.Monster.MaxHp = 100;
        var options = CreateUnifiedMonsterOptions(
        [
            new() { Type = "ApplyStatus", Target = "AllAlive", StatusCode = "future-poison", DurationRounds = 2, AttackPowerPercent = 50 },
            new() { Type = "ApplyStatus", Target = "Self", StatusCode = "future-regrowth", DurationRounds = 2, HealMaxHpPercent = 10 }
        ]);
        options.StatusEffects =
        [
            new() { Code = "future-poison", Name = "Poison", Description = "Attack snapshot", EffectType = "DamageOverTime", ValuePerStack = 1 },
            new() { Code = "future-regrowth", Name = "Regrowth", Description = "Health snapshot", EffectType = "HealOverTime", ValuePerStack = 1, IsPositive = true }
        ];
        var service = new MonsterCombatService(test.Db, new MonsterCombatCatalog(Options.Create(options)));
        var party = await UnifiedPartyAsync(test.Db);
        await service.ExecuteIntentAsync(test.Room, test.Monster, party, new Dictionary<int, ElementType>(), []);
        await service.ResolveEndOfRoundAsync(test.Room, test.Monster, party, []);
        Assert.Equal(100, test.Character.Hp);
        Assert.Equal(40, test.Monster.Hp);
        await test.Db.SaveChangesAsync();
        var snapshots = await test.Db.BattleStatusEffects.ToListAsync();
        Assert.All(snapshots, state =>
        {
            Assert.Equal(10, state.PerTickValue);
            Assert.Equal("Monster", state.SourceActorType);
            Assert.Equal("boss-combination", state.SourceSkillCode);
        });
        test.Monster.Attack = 999;
        test.Monster.MaxHp = 200;
        test.Room.RoundNumber++;

        await service.ResolveEndOfRoundAsync(test.Room, test.Monster, party, []);

        Assert.Equal(90, test.Character.Hp);
        Assert.Equal(50, test.Monster.Hp);
    }

    private static MonsterCombatOptions CreateUnifiedMonsterOptions(List<CombatSkillEffectOptions> effects) => new()
    {
        Skills = [new() { Code = "boss-combination", Name = "Combination", Description = "Ordered effects", TargetType = "AllAlive", Effects = effects, CooldownRounds = 2 }],
        Profiles = new() { ["boss"] = new() { SkillUseChancePercent = 100, Skills = [new() { Code = "boss-combination" }] } }
    };

    private static BattleEffectExecutor UnifiedExecutor(BattleStatusService statuses, SkillCatalog? skills = null)
    {
        var guards = new BattleGuardService(statuses);
        return new(skills ?? new SkillCatalog(Options.Create(new SkillOptions())), statuses, guards, new(statuses, guards));
    }

    private static async Task<List<BattleParticipant>> UnifiedPartyAsync(GameDbContext db) => await
        (from slot in db.RoomSlots join character in db.Characters on slot.CharacterId equals character.Id
            orderby slot.SlotIndex select new BattleParticipant(slot, character)).ToListAsync();
}
