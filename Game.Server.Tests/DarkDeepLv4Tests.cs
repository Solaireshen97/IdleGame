using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private static MonsterCombatOptions PlagueLv4Options()
    {
        var options = PlagueLv3Options();
        var poison = options.Profiles["fire-test"].PlaguePoison!;
        poison.TriggerHpPercent = null;
        poison.FirstActivationRound = 6;
        poison.CycleRounds = 10;
        poison.BreakLightDamagePercent = 6;
        return options;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task DarkPlagueLv4FixedCyclesAndFullRewardsSurviveReloads(int breakRound, bool cleanse)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp:1000);
        test.Character.MaxHp = 1000;
        var rig = await FireRigAsync(test, PlagueLv4Options());
        test.Room.RoundNumber = 60; test.Monster.Hp = 7000;
        await test.Db.SaveChangesAsync();
        int? lastBreak = null;
        for (var local = 1; local <= 35; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
            var character = await db.Characters.SingleAsync();
            var party = new List<BattleParticipant> { new(await db.RoomSlots.SingleAsync(), character) };
            room.RoundNumber = 60 + local - 1;
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            await phases.BeginRoundAsync(room, monster, [], party);
            await phases.BeginRoundAsync(room, monster, [], party);
            var state = Assert.Single(db.BattleMonsterPhaseStates.Local);
            var cycle = local < 6 ? 0 : (local - 6) / 10 + 1;
            var phaseRound = local < 6 ? 0 : (local - 6) % 10 + 1;
            Assert.Equal(cycle, state.ActivationCount);
            Assert.Equal(6 + cycle * 10, state.NextActivationRound);
            Assert.Equal(lastBreak is { } b && local > b && local <= b + 3 ? 15m : 0m,
                await statuses.ModifierAsync(room, "Character", character.Id, "ReductionPercent"));
            if (phaseRound == 1)
            {
                var poison = Assert.Single(await statuses.GetActiveAsync(room,"Character",[character.Id]),s=>s.EffectCode=="plague-test");
                Assert.Equal((1,10,room.RoundNumber+3),(poison.Stacks,poison.PerTickValue,poison.ExpiresAfterRound));
                Assert.Equal(0,state.ElementDamage);
                Assert.Equal(1000,TalentRules.EffectiveMaxHp(character));
                Assert.False(await statuses.HasAsync(room,"Monster",monster.Id,"plague-tick"));
            }
            var context = new BattleExecutionContext(room,monster,party,new Dictionary<int,ElementType>(),new Dictionary<int,OperationPotionBonuses>(),[]);
            if (state.IsActive && phaseRound == breakRound)
            {
                var hp = character.Hp;
                if (cleanse)
                {
                    var removed = await statuses.RemoveFirstAsync(room,"Character",[character.Id],false);
                    Assert.Equal("plague-test",removed!.Code);
                    await phases.ObservePlagueCleanseAsync(context,character.Id,removed.Code);
                }
                else
                {
                    await phases.ObserveDirectDamageAsync(context,ElementType.Light,599);
                    Assert.True(state.IsActive); Assert.Equal(599,state.ElementDamage);
                    await phases.ObserveDirectDamageAsync(context,ElementType.Light,1);
                    await phases.ObserveDirectDamageAsync(context,ElementType.Light,9999);
                    Assert.Equal(600,state.ElementDamage);
                }
                Assert.Equal(hp,character.Hp);
                Assert.Equal(1000,context.Characters.Single().MaxHp);
                Assert.Equal(cycle,state.BreakCount); lastBreak = local;
            }
            await statuses.ResolveEndOfRoundAsync(room,monster,party,[]);
            await phases.EndRoundAsync(room,monster,[],party);
            var hpAfter = character.Hp;
            await phases.EndRoundAsync(room,monster,[],party);
            Assert.Equal(hpAfter,character.Hp);
            if (state.IsActive)
                Assert.Equal(phaseRound < 2 ? 1000 : phaseRound == 2 ? 900 : 800,TalentRules.EffectiveMaxHp(character));
            else Assert.Equal(1000,TalentRules.EffectiveMaxHp(character));
            // Model healing between rounds without altering cooldowns or the phase schedule.
            character.Hp = TalentRules.EffectiveMaxHp(character);
            await db.SaveChangesAsync();
        }
        await using var finalDb = test.CreateDbContext();
        var final = await finalDb.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal((3,3,0,0,false),(final.ActivationCount,final.BreakCount,final.ExpiryCount,final.LinkedHitCount,final.IsActive));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkPlagueLv4DeathEndsCurrentTargetAndNextCycleSelectsSurvivingFront(bool earlyDeath)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp:1000);
        var second = await test.AddSlotAsync(2,"Next front",hp:1000);
        test.Character.MaxHp=second.MaxHp=1000;
        var rig=await FireRigAsync(test,PlagueLv4Options());
        for(var local=1;local<=16;local++)
        {
            test.Room.RoundNumber=60+local-1;
            await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
            if(local==6 && earlyDeath)test.Character.Hp=0;
            foreach(var p in rig.Party.Where(p=>p.Character.Hp>0))p.Character.Hp=TalentRules.EffectiveMaxHp(p.Character);
            await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
            await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
            var state=Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
            Assert.Equal(local<6?0:local<16?1:2,state.ActivationCount);
            if(local>=9)
            {
                Assert.Equal(0,test.Character.Hp); Assert.Null(test.Character.BattleMaxHpLimit);
                Assert.Null(state.RewardStartsAtRound);
                Assert.Equal(earlyDeath?0:1,state.LinkedHitCount);
            }
            if(local<16)Assert.False(await rig.Statuses.HasAsync(test.Room,"Character",second.Id,"plague-test"));
            else Assert.Equal(2,await rig.Statuses.StacksAsync(test.Room,"Character",second.Id,"plague-test"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkPlagueLv4BossDeathCancelsActiveOrPendingRewardAndFutureCycles(bool pendingReward)
    {
        await using var test=await BattleTestContext.CreateAsync(characterHp:1000);
        test.Character.MaxHp=1000;var rig=await FireRigAsync(test,PlagueLv4Options());
        for(var local=1;local<=8;local++)
        {test.Room.RoundNumber=local-1;await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);}
        Assert.Equal(800,TalentRules.EffectiveMaxHp(test.Character));
        test.Room.RoundNumber=8;
        await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
        if(pendingReward)await rig.Phases.ObserveDirectDamageAsync(rig.Context,ElementType.Light,600);
        test.Monster.Hp=0;var hp=test.Character.Hp;
        await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
        for(var local=10;local<=36;local++){test.Room.RoundNumber=local-1;await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);}
        Assert.Equal(hp,test.Character.Hp);Assert.Equal(1000,TalentRules.EffectiveMaxHp(test.Character));
        Assert.Empty(await rig.Statuses.GetActiveAsync(test.Room,"Character",[test.Character.Id]));
        var state=Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1,0,false),(state.ActivationCount,state.LinkedHitCount,state.IsActive));Assert.Null(state.RewardStartsAtRound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkPlagueLv4FrozenSnapshotsWithoutScheduleKeepSingleHpPhase(bool oldLv3)
    {
        await using var test=await BattleTestContext.CreateAsync(characterHp:1000);
        test.Character.MaxHp=1000;
        var rig=await FireRigAsync(test,oldLv3?PlagueLv3Options():PlagueTestOptions());
        var depths=new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));var rewards=RewardTestFactory.CreateCatalog();
        var oldRules=new DungeonRunRulesService(test.Db,rig.Catalog,rewards,PartyScalingCatalog.Default,depths);
        await oldRules.EnsureAsync(test.Room);
        var row=Assert.Single(test.Db.DungeonRunRuleSnapshots.Local);var json=JsonNode.Parse(row.DefinitionJson)!;
        var poison=json["Combat"]!["Profiles"]!["fire-test"]!["PlaguePoison"]!.AsObject();
        poison.Remove("FirstActivationRound");poison.Remove("CycleRounds");row.DefinitionJson=json.ToJsonString();
        row.Revision=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.DefinitionJson))).ToLowerInvariant();
        await test.Db.SaveChangesAsync();
        var current=new MonsterCombatCatalog(Options.Create(PlagueLv4Options()));
        var rules=new DungeonRunRulesService(test.Db,current,rewards,PartyScalingCatalog.Default,depths);
        var statuses=new BattleStatusService(test.Db,current.Statuses,runRules:rules);var phases=new MonsterPhaseService(test.Db,current,statuses,rules);
        test.Monster.Hp=7000;
        for(var local=1;local<=35;local++)
        {
            test.Room.RoundNumber=60+local-1;await phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
            if(local==1)await phases.ObserveDirectDamageAsync(rig.Context,ElementType.Light,400);
            await phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
        }
        Assert.Equal(70,phases.PlaguePoisonDefinition(test.Room,test.Monster)!.TriggerHpPercent);
        var state=Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1,1,0),(state.ActivationCount,state.BreakCount,state.NextActivationRound));
        Assert.Equal(row.Revision,(await rules.EnsureAsync(test.Room)).Revision);
    }

    [Fact]
    public void DarkPlagueLv4RejectsMixedSchedulesAndExportsIndependentCopies()
    {
        foreach(var fault in new Action<PlaguePoisonOptions>[] {
            p=>p.TriggerHpPercent=70,p=>p.FirstActivationRound=0,p=>p.CycleRounds=6,
            p=>p.CycleRounds=-1,p=>p.ErosionStartStacks=0 })
        {var o=PlagueLv4Options();fault(o.Profiles["fire-test"].PlaguePoison!);Assert.Throws<InvalidOperationException>(()=>new MonsterCombatCatalog(Options.Create(o)));}
        var options=PlagueLv4Options();var catalog=new MonsterCombatCatalog(Options.Create(options));
        options.Profiles["fire-test"].PlaguePoison!.CycleRounds=8;
        catalog.ExportOptions().Profiles["fire-test"].PlaguePoison!.FirstActivationRound=3;
        var copy=new MonsterCombatCatalog(Options.Create(catalog.ExportOptions())).FindProfile("fire-test")!.PlaguePoison!;
        Assert.Equal((6,10,6m),(copy.FirstActivationRound,copy.CycleRounds,copy.BreakLightDamagePercent));Assert.Null(copy.TriggerHpPercent);
        Assert.Contains("瘟疫循环",catalog.GetAddedMechanics("fire-test",1));
    }
}
