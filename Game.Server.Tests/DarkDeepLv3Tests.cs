using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private static MonsterCombatOptions PlagueLv3Options()
    {
        var o = PlagueTestOptions();
        o.StatusEffects[0].MaxStacks = 5;
        o.StatusEffects.Add(new() { Code="plague-erosion", Name="Erosion", Description="Erosion", EffectType="None",
            ValuePerStack=10, MaxStacks=2, IsPositive=false, IsDispellable=false,
            Lifetime=BattleStatusLifetime.Encounter, Mechanic=BattleStatusMechanic.PlagueErosion });
        var p = o.Profiles["fire-test"].PlaguePoison!;
        p.BreakLightDamagePercent=4; p.ErosionStartStacks=3; p.ErosionPercentPerStack=10;
        p.ErosionStatusCode="plague-erosion"; p.LethalStacks=5;
        return o;
    }

    private static async Task<FireRig> PlagueLv3RigAsync(BattleTestContext test)
    {
        test.Character.MaxHp=1000; test.Character.Hp=1000;
        var rig = await FireRigAsync(test, PlagueLv3Options());
        test.Monster.Hp=7000;
        test.Room.RoundNumber=60;
        return rig;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkPlagueLv3LastRoundLightOrCleanseRestoresCapWithoutHealingAndGetsFullReward(bool cleanse)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig=await PlagueLv3RigAsync(test);
        var context=rig.Context;
        for(var r=60;r<=62;r++)
        {
            test.Room.RoundNumber=r;
            await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room,test.Monster,rig.Party,[]);
            await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
            Assert.Equal(r<61?1000:r==61?900:800,TalentRules.EffectiveMaxHp(test.Character));
        }
        test.Room.RoundNumber=63;
        await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
        var hp=test.Character.Hp;
        var actor=context.Characters.Single();
        Assert.Equal(800,actor.MaxHp);
        if(cleanse)
        {
            var removed=await rig.Statuses.RemoveFirstAsync(test.Room,"Character",[test.Character.Id],false);
            await rig.Damage.ObserveCleanseAsync(context,test.Character.Id,removed!.Code);
        }
        else
        {
            await rig.Phases.ObserveDirectDamageAsync(context,ElementType.Light,399);
            Assert.Equal(800,actor.MaxHp);
            Assert.Equal(4,await rig.Statuses.StacksAsync(test.Room,"Character",test.Character.Id,"plague-test"));
            await rig.Phases.ObserveDirectDamageAsync(context,ElementType.Light,1);
        }
        Assert.Equal(1000,actor.MaxHp); // Existing actors and immutable combat inputs observe the live restoration.
        Assert.Equal(1000,context.StatsFor(test.Character).MaxHp);
        Assert.Equal(hp,test.Character.Hp);
        Assert.Equal(1000,test.Character.MaxHp);
        await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
        var state=Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1,0,0),(state.BreakCount,state.ExpiryCount,state.LinkedHitCount));
        for(var r=64;r<=67;r++)
        {
            test.Room.RoundNumber=r;
            await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
            Assert.Equal(r<=66?15m:0m,await rig.Statuses.ModifierAsync(test.Room,"Character",test.Character.Id,"ReductionPercent"));
        }
    }

    [Fact]
    public async Task DarkPlagueLv3FourFullRoundsEndInDeathDespiteHealingAndReductionAndNeverRetarget()
    {
        await using var test=await BattleTestContext.CreateAsync();
        var rig=await PlagueLv3RigAsync(test);
        var second=new Character { UserId=test.Character.UserId,Name="Second",MaxHp=1000,Hp=1000 };
        test.Db.Characters.Add(second);await test.Db.SaveChangesAsync();
        rig.Party.Add(new(new RoomSlot { RoomId=test.Room.Id,SlotIndex=2,CharacterId=second.Id },second));
        test.Character.CombatWeaponDirectReductionPercent=100;
        for(var r=60;r<=63;r++)
        {
            test.Room.RoundNumber=r;
            await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
            Assert.True(test.Character.Hp>0);
            test.Character.Hp=TalentRules.EffectiveMaxHp(test.Character);
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room,test.Monster,rig.Party,[]);
            await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
            await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
        }
        Assert.Equal(0,test.Character.Hp);Assert.Equal(1000,second.Hp);
        Assert.Null(test.Character.BattleMaxHpLimit);
        var state=Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1,0,0,1,false),(state.ActivationCount,state.BreakCount,state.ExpiryCount,state.LinkedHitCount,state.IsActive));
        test.Room.RoundNumber=64;
        await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
        Assert.Equal(0,await rig.Statuses.StacksAsync(test.Room,"Character",second.Id,"plague-test"));
        Assert.Null(state.RewardStartsAtRound);
    }

    [Fact]
    public async Task DarkPlagueLv3PersistedErosionRestoresReadOnlyCapAcrossReloadAndNeverCompounds()
    {
        await using var test=await BattleTestContext.CreateAsync();
        var rig=await PlagueLv3RigAsync(test);
        test.Character.WeaponHealthBonusPercent=100;
        for(var r=60;r<=61;r++)
        {
            test.Room.RoundNumber=r;
            await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
            await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
        }
        Assert.Equal(1800,TalentRules.EffectiveMaxHp(test.Character));
        test.Room.RoundNumber=62;await test.Db.SaveChangesAsync();
        await using var db=test.CreateDbContext();
        var room=await db.Rooms.SingleAsync();var monster=await db.Monsters.SingleAsync();var c=await db.Characters.SingleAsync();
        var party=new List<BattleParticipant> { new(await db.RoomSlots.SingleAsync(),c) };
        var statuses=new BattleStatusService(db,rig.Catalog.Statuses);
        var phases=new MonsterPhaseService(db,rig.Catalog,statuses);
        Assert.Null(c.BattleMaxHpLimit);var before=c.Hp;
        using(await statuses.BeginReadSnapshotAsync(room))
        {
            await phases.RefreshPlagueHealthAsync(room,monster,party);
            await phases.RefreshPlagueHealthAsync(room,monster,party);
            Assert.Equal(1800,TalentRules.EffectiveMaxHp(c));Assert.Equal(before,c.Hp);
        }
        Assert.Equal(EntityState.Unchanged,db.Entry(c).State);
        await phases.BeginRoundAsync(room,monster,[],party);
        await phases.EndRoundAsync(room,monster,[],party);
        await phases.EndRoundAsync(room,monster,[],party);
        Assert.Equal(1600,TalentRules.EffectiveMaxHp(c));
        Assert.Equal(2000,(await statuses.GetActiveAsync(room,"Character",[c.Id])).Single(s=>s.EffectCode=="plague-erosion").MagnitudeSnapshot);
        var hp=c.Hp;await statuses.ClearRunAsync(room);
        Assert.Equal(2000,TalentRules.EffectiveMaxHp(c));Assert.Equal(hp,c.Hp);
    }

    [Fact]
    public async Task DarkPlagueLv3LiveCapControlsHealingPercentAndCeilingAndRestoresOnExistingActor()
    {
        await using var test=await BattleTestContext.CreateAsync();var rig=await PlagueLv3RigAsync(test);
        var target=rig.Context.Characters.Single();var heal=new BattleSkillEffect(BattleEffectKind.Heal,
            new(BattleTargetSide.Self,BattleTargetSelection.Self,false,false,"Self"),HealMaxHpPercent:20);
        test.Character.BattleMaxHpLimit=800;test.Character.Hp=700;
        Assert.Equal(160,BattleDamageService.CalculateHealing(target,target,heal));
        Assert.Equal(100,BattleDamageService.Heal(target,target,heal));Assert.Equal(800,test.Character.Hp);
        test.Character.BattleMaxHpLimit=null;
        Assert.Equal(200,BattleDamageService.CalculateHealing(target,target,heal));
        Assert.Equal(200,BattleDamageService.Heal(target,target,heal));Assert.Equal(1000,test.Character.Hp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkPlagueLv3BossOrTargetDeathClearsErosionWithoutRevivalOrExtraTicks(bool bossDies)
    {
        await using var test=await BattleTestContext.CreateAsync();var rig=await PlagueLv3RigAsync(test);
        for(var r=60;r<=62;r++){test.Room.RoundNumber=r;await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);}
        test.Room.RoundNumber=63;var hp=test.Character.Hp;
        if(bossDies)test.Monster.Hp=0;else test.Character.Hp=0;
        await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
        Assert.Equal(bossDies?hp:0,test.Character.Hp);Assert.Equal(1000,TalentRules.EffectiveMaxHp(test.Character));
        Assert.False(Assert.Single(test.Db.BattleMonsterPhaseStates.Local).IsActive);
        Assert.Equal(0,Assert.Single(test.Db.BattleMonsterPhaseStates.Local).LinkedHitCount);
    }

    [Fact]
    public void DarkPlagueLv3CatalogRejectsPartialLinkageAndWrongCounters()
    {
        foreach(var fault in new Action<MonsterCombatOptions>[] {
            o=>o.Profiles["fire-test"].PlaguePoison!.LethalStacks=4,
            o=>o.Profiles["fire-test"].PlaguePoison!.ErosionStartStacks=1,
            o=>o.Profiles["fire-test"].PlaguePoison!.ErosionPercentPerStack=50,
            o=>o.StatusEffects[^1].IsDispellable=true,
            o=>o.StatusEffects[^1].MaxStacks=3,
            o=>o.StatusEffects[0].MaxStacks=4 })
        {var o=PlagueLv3Options();fault(o);Assert.Throws<InvalidOperationException>(()=>new MonsterCombatCatalog(Options.Create(o)));}
    }

    [Fact]
    public async Task DarkPlagueLv3ClosingRoomRestoresLimitWithoutChangingBaseLifeOrHealing()
    {
        await using var test=await BattleTestContext.CreateAsync();var rig=await PlagueLv3RigAsync(test);
        for(var r=60;r<=62;r++){test.Room.RoundNumber=r;await rig.Phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);await rig.Phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);}
        Assert.Equal(800,TalentRules.EffectiveMaxHp(test.Character));var hp=test.Character.Hp;
        await CharacterActivityManager.CloseBattleRoomAsync(test.Db,test.Room,DateTime.UtcNow);
        Assert.Equal(1000,TalentRules.EffectiveMaxHp(test.Character));Assert.Equal(1000,test.Character.MaxHp);
        Assert.Equal(hp,test.Character.Hp);
    }

    [Fact]
    public async Task DarkPlagueLv3NewLinkageDoesNotUpgradeFrozenLv2Poison()
    {
        await using var test=await BattleTestContext.CreateAsync(characterHp:1000);
        var rig=await PlagueRigAsync(test);
        var depths=new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards=RewardTestFactory.CreateCatalog();
        var oldRules=new DungeonRunRulesService(test.Db,rig.Catalog,rewards,PartyScalingCatalog.Default,depths);
        var old=await oldRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        var current=new MonsterCombatCatalog(Options.Create(PlagueLv3Options()));
        var rules=new DungeonRunRulesService(test.Db,current,rewards,PartyScalingCatalog.Default,depths);
        var statuses=new BattleStatusService(test.Db,current.Statuses,runRules:rules);
        var phases=new MonsterPhaseService(test.Db,current,statuses,rules);
        await rules.EnsureAsync(test.Room);
        Assert.Equal(0,phases.PlaguePoisonDefinition(test.Room,test.Monster)!.LethalStacks);
        for(var i=0;i<4;i++)
        {
            await phases.BeginRoundAsync(test.Room,test.Monster,[],rig.Party);
            await phases.EndRoundAsync(test.Room,test.Monster,[],rig.Party);
            Assert.Equal(1000,TalentRules.EffectiveMaxHp(test.Character));
            Assert.True(test.Character.Hp>0);
            test.Room.RoundNumber++;
        }
        var state=Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((0,1,0,false),(state.BreakCount,state.ExpiryCount,state.LinkedHitCount,state.IsActive));
        Assert.Equal(old.Revision,(await rules.EnsureAsync(test.Room)).Revision);
    }
}
