using System.Data.Common;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task ActualRoundStatusSelectBudgetDoesNotGrowWithPartyAttackCount()
    {
        var single = await ExecuteBudgetRoundAsync(1);
        var fullParty = await ExecuteBudgetRoundAsync(5);

        // The real round entry loads one settlement batch; neither party size nor
        // the number of normal attacks should introduce extra state-table reads.
        Assert.Equal(1, single);
        Assert.Equal(1, fullParty);
        Assert.Equal(single, fullParty);
    }

    private static async Task<int> ExecuteBudgetRoundAsync(int partySize)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 12, monsterDefense: 0);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        for (var slot = 2; slot <= partySize; slot++)
            await test.AddSlotAsync(slot, $"Ally {slot}", attack: 20);
        await test.Db.SaveChangesAsync();

        var counter = new RoundStatusSelectCounter();
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(test.Db.Database.GetConnectionString()!)
            .AddInterceptors(counter).Options;
        await using var db = new GameDbContext(options);
        var progression = ProgressionTestFactory.Create();
        var skills = new SkillCatalog(Options.Create(new SkillOptions()));
        var combat = new MonsterCombatService(db, new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions())));
        var service = new BattleService(db, new UserService(db, progression, skills), ConsumableTestFactory.Create(),
            skills, RewardTestFactory.CreateService(db, progression), monsterCombatService: combat);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.NotNull(result);
        var statusSelects = counter.Count;
        var monster = await db.Monsters.SingleAsync();
        var party = await UnifiedPartyAsync(db);
        Assert.Equal(1000 - partySize * 20, monster.Hp);
        Assert.Equal(88, party[0].Character.Hp);
        Assert.All(party.Skip(1), member => Assert.Equal(100, member.Character.Hp));
        var hits = result.Events.Where(item => item.Kind == BattleEventKind.Damage).ToArray();
        Assert.Equal(partySize + 1, hits.Length);
        Assert.Equal(party.Select(member => member.Character.Id),
            hits.Where(item => item.Source!.ActorType == "Character").Select(item => item.Source!.ActorId));
        Assert.All(hits.Where(item => item.Source!.ActorType == "Character"), item =>
        {
            Assert.Equal(20, item.ActualAmount);
            Assert.Equal("Monster", item.Target!.ActorType);
            Assert.Equal(monster.Id, item.Target.ActorId);
        });
        var monsterHit = Assert.Single(hits, item => item.Source!.ActorType == "Monster");
        Assert.Equal(12, monsterHit.ActualAmount);
        Assert.Equal(party[0].Character.Id, monsterHit.Target!.ActorId);
        return statusSelects;
    }

    private sealed class RoundStatusSelectCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        private void Record(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("BattleStatusEffects", StringComparison.OrdinalIgnoreCase)) Count++;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}
