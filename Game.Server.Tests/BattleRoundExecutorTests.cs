using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(20, 12, BattleRoundOutcome.Continue, 180, 88)]
    [InlineData(200, 12, BattleRoundOutcome.MonsterDefeated, 0, 100)]
    [InlineData(20, 100, BattleRoundOutcome.PartyDefeated, 180, 0)]
    public async Task RoundCalculationLeavesPersistenceAndRoomProgressToCaller(int attack, int enemyAttack,
        BattleRoundOutcome expectedOutcome, int monsterHp, int characterHp)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: attack,
            characterDefense: 0, monsterAttack: enemyAttack, monsterDefense: 0);
        test.Monster.Hp = test.Monster.MaxHp = 200;
        await test.Db.SaveChangesAsync();
        var skills = new SkillCatalog(Options.Create(new SkillOptions()));
        var statuses = new BattleStatusService(test.Db,
            new BattleStatusCatalog(Options.Create(new MonsterCombatOptions())));
        var executor = new BattleRoundExecutor(test.Db, ConsumableTestFactory.Create(), skills,
            statuses, UnifiedExecutor(statuses, skills));
        var party = await UnifiedPartyAsync(test.Db);
        var round = test.Room.RoundNumber;
        var version = test.Room.Version;
        var status = test.Room.Status;
        var wave = test.Room.CurrentWaveNumber;
        using var settlement = await statuses.BeginSettlementAsync(test.Room);
        using var recording = statuses.Events.Begin(test.Room, test.Monster, party);

        var outcome = await executor.ExecuteAsync(test.Room, test.Monster, party.ToList(), [], []);

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(monsterHp, test.Monster.Hp);
        Assert.Equal(characterHp, test.Character.Hp);
        Assert.Equal(round, test.Room.RoundNumber);
        Assert.Equal(version, test.Room.Version);
        Assert.Equal(status, test.Room.Status);
        Assert.Equal(wave, test.Room.CurrentWaveNumber);
        Assert.False(party[0].Slot.HasParticipatedInRun);
        Assert.NotEmpty(statuses.Events.Snapshot(test.Room));
        // A separate connection must still see the pre-round state. Reward, room,
        // HP and inventory changes are committed only by the outer coordinator.
        await using var fresh = test.CreateDbContext();
        Assert.Equal(200, (await fresh.Monsters.SingleAsync()).Hp);
        Assert.Equal(100, (await fresh.Characters.SingleAsync()).Hp);
        Assert.Empty(await fresh.BattleOperationPotionStates.ToListAsync());
    }
}
