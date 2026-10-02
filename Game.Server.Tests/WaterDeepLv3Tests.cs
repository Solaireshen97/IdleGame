using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private const string Lv3Cold = "water-deep-lv3-cold", Lv3Freeze = "water-deep-lv3-freeze", Lv3Warm = "water-deep-lv3-warm";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WaterLv3TwoEarthClearOnThirdRoundOneEarthHoldsAndNoEarthFreezesOnlyOnce(int earthActors)
    {
        await using var test = await BattleTestContext.CreateAsync();
        for (var i = 2; i <= 5; i++) await test.AddSlotAsync(i, "队友" + i);
        var rig = await ColdRigAsync(test, "water-deep-lv3-boss");
        test.Monster.Hp = 7000;
        for (var round = 0; round < 4; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(earthActors == 0 && round == 1,
                await rig.Statuses.IsActionBlockedAsync(test.Room, test.Character.Id));
            for (var i = 0; i < earthActors; i++)
                await rig.Damage.CharacterDamageAsync(rig.Context, BattleActor.ForCharacter(rig.Party[i]),
                    BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, false, damageElement: ElementType.Earth);
            if (earthActors == 2 && round >= 2)
                Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, Lv3Cold));
            await ColdEndAsync(test, rig);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []); // Duplicate end must not grow twice.
            var expected = round == 3 ? 0 : earthActors == 0 ? 5 : earthActors == 1 ? 4 : round == 0 ? 3 : round == 1 ? 2 : 0;
            Assert.Equal(expected, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, Lv3Cold));
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(earthActors == 2 ? 5 : 0, state.BreakCount);
        Assert.False(state.IsActive);
        for (var round = 4; round <= 6; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(earthActors == 2 && round <= 5 ? 20m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Character", test.Character.Id, "DamageDealtPercent"));
            Assert.False(await rig.Statuses.IsActionBlockedAsync(test.Room, test.Character.Id));
            await ColdEndAsync(test, rig);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarthThawRestoresOnlyActionsThatHaveNotAlreadyBeenSkipped(bool alreadySkipped)
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.AddSlotAsync(2, "未冻结的土角色");
        var rig = await ColdRigAsync(test, "water-deep-lv3-boss");
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var source = BattleActor.ForCharacter(rig.Party[1]);
        await rig.Effects.ExecuteAsync(new(rig.Context, ColdCharacterSkill(BattleEffectKind.Cleanse), source));
        await ColdEndAsync(test, rig);
        test.Room.RoundNumber = 1;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.True(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, Lv3Freeze));
        if (alreadySkipped) Assert.True(await rig.Statuses.SkipBlockedActionAsync(test.Room, test.Character.Id));
        await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(100), BattleDamageOrigin.Skill,
            false, damageElement: ElementType.Earth);
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, Lv3Freeze));
        Assert.Equal(4, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, Lv3Cold));
        Assert.Equal(alreadySkipped, await rig.Statuses.IsActionBlockedAsync(test.Room, test.Character.Id));
        await ColdEndAsync(test, rig);
        test.Room.RoundNumber = 2;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.False(await rig.Statuses.IsActionBlockedAsync(test.Room, test.Character.Id));
    }

    [Fact]
    public async Task ExternalCleanseRemovesColdAndFreezeAndGrantsWarmFromNextFullRound()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.AddSlotAsync(2, "已净化的治疗");
        var rig = await ColdRigAsync(test, "water-deep-lv3-boss");
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var healer = BattleActor.ForCharacter(rig.Party[1]);
        var cast = new BattleCastExecution(rig.Context, ColdCharacterSkill(BattleEffectKind.Cleanse), healer);
        await rig.Effects.ExecuteAsync(cast);
        await ColdEndAsync(test, rig);
        test.Room.RoundNumber = 1;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.True(await rig.Statuses.IsActionBlockedAsync(test.Room, test.Character.Id));
        await rig.Effects.CleanseAsync(cast, BattleActor.ForCharacter(rig.Party[0]));
        Assert.False(await rig.Statuses.IsActionBlockedAsync(test.Room, test.Character.Id));
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, Lv3Cold));
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, Lv3Warm));
        await ColdEndAsync(test, rig);
        test.Room.RoundNumber = 2;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal(20m, await rig.Statuses.ModifierAsync(test.Room, "Character", test.Character.Id, "DamageDealtPercent"));
    }

    [Fact]
    public async Task FreezeQueueAndSingleUseSurviveDatabaseReload()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await ColdRigAsync(test, "water-deep-lv3-boss");
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await ColdEndAsync(test, rig);
        await test.Db.SaveChangesAsync();
        for (var round = 1; round <= 5; round++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            var monster = await db.Monsters.SingleAsync();
            var character = await db.Characters.SingleAsync();
            room.RoundNumber = round;
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            await phases.BeginRoundAsync(room, monster, []);
            Assert.Equal(round == 1, await statuses.HasAsync(room, "Character", character.Id, Lv3Freeze));
            await statuses.ResolveEndOfRoundAsync(room, monster, [new(await db.RoomSlots.SingleAsync(), character)], []);
            await phases.EndRoundAsync(room, monster, []);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task FrozenActorSkipsNativeSkillsSoulNormalEchoAndCounterButKeepsPotionAndOngoingHealing()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 30, characterAttack: 100, monsterAttack: 1);
        test.Character.ProfessionCode = "swordsman";
        test.Character.WeaponNormalEchoPercent = 100;
        await test.AddPotionAsync(test.Character, 1, true, 90);
        await test.AddSkillAsync(test.Character, 1, "sword-slash", true);
        test.Room.RoundNumber = 5;
        var rig = await ColdRigAsync(test, "water-deep-lv3-boss");
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var skills = new SkillCatalog(Options.Create(config.GetSection("Skills").Get<Game.Server.Configuration.SkillOptions>()!));
        var guards = new BattleGuardService(rig.Statuses);
        var effects = new BattleEffectExecutor(skills, rig.Statuses, guards, rig.Damage);
        var monster = new MonsterCombatService(test.Db, rig.Catalog, new Random(7213), rig.Statuses, skills, effects, phases: rig.Phases);
        var executor = new BattleRoundExecutor(test.Db, ConsumableTestFactory.Create(), skills, rig.Statuses, effects, monster,
            soulImprintCatalog: SoulImprintTestFactory.Create());
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint { CharacterId = test.Character.Id, SoulImprintCode = "deep-core", EquippedSlotIndex = 1, AutoUseEnabled = true });
        var cooldown = new BattleSkillCooldown { RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillCode = "sword-slash", ReadyAtRound = 5 };
        test.Db.BattleSkillCooldowns.Add(cooldown);
        test.Room.RoundNumber = 4;
        await rig.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "knight-holy-renew", 2, [], "", perTickValue: 10);
        test.Room.RoundNumber = 5;
        await rig.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, Lv3Freeze, 0, [], "");
        await guards.ApplyAsync(test.Room, test.Character.Id, 30, new("Character", test.Character.Id), true);
        await test.Db.SaveChangesAsync();
        using var settlement = await rig.Statuses.BeginSettlementAsync(test.Room);
        using var recording = rig.Statuses.Events.Begin(test.Room, test.Monster, rig.Party);
        await executor.ExecuteAsync(test.Room, test.Monster, rig.Party, [test.Character.Id], []);
        Assert.Equal(10000, test.Monster.Hp);
        Assert.Equal(5, cooldown.ReadyAtRound);
        Assert.Equal(59, test.Character.Hp); // 30 + 20 potion - 1 monster + 10 existing HoT.
        Assert.DoesNotContain(rig.Statuses.Events.Snapshot(test.Room), e => e.Source?.ActorType == "Character" && e.Kind == Game.Shared.Dtos.BattleEventKind.Damage);
        Assert.DoesNotContain(test.Db.BattleSkillCooldowns.Local, c => c.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix));
    }

    [Fact]
    public void WaterLv3RejectsIncompleteFreezeDeclarationsAndRetainsLv2Rules()
    {
        foreach (var fault in new Action<Game.Server.Configuration.DeepColdOptions>[]
        {
            c => c.FreezeAtStacks = 4, c => c.GrowthStacksPerRound = 0,
            c => c.FreezePendingStatusCode = c.PendingStatusCode, c => c.FreezeUsedStatusCode = "missing",
            c => c.GrowthUsedStatusCode = c.MeltUsedStatusCode
        })
        {
            var options = WaterLv1Catalog().ExportOptions();
            fault(options.Profiles["water-deep-lv3-boss"].DeepCold!);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var catalog = WaterLv1Catalog();
        Assert.Equal(0, catalog.FindProfile("water-deep-lv2-boss")!.DeepCold!.GrowthStacksPerRound);
        Assert.Equal(15m, catalog.FindStatus(WarmCode)!.ValuePerStack);
        Assert.Equal(20m, catalog.FindStatus(Lv3Warm)!.ValuePerStack);
        Assert.Equal(2, catalog.GetAddedMechanics("water-deep-lv1-monster-5", 3).Count);
    }
}
