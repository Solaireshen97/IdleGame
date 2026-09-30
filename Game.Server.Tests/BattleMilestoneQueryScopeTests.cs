using System.Data.Common;
using System.Text.RegularExpressions;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task SyncFiltersClearMilestonesToThePartyAndPreservesAutoEligibility(int partySize)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterDefense: 0);
        test.Room.IsPreparationTimeoutEnabled = false;
        test.Room.IsOwnerAutoEnabled = true;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        // Neither a same-account character outside the room nor another account's
        // clear may unlock the room's actual members.
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones.ToListAsync());
        var partyIds = new List<int> { test.Character.Id };
        for (var slot = 2; slot <= partySize; slot++)
            partyIds.Add((await test.AddSlotAsync(slot, $"Party {slot}")).Id);
        test.Db.Users.Add(new User { Id = 99, UserName = "outside", PasswordHash = "x" });
        for (var id = 100; id < 150; id++)
        {
            test.Db.Characters.Add(new Character { Id = id, UserId = id == 100 ? 1 : 99,
                Name = $"Outside {id}", Hp = 100, MaxHp = 100 });
            test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone { CharacterId = id,
                Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1 });
        }
        await test.Db.SaveChangesAsync();
        var counter = new PartyMilestoneQueryCounter();
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(test.Db.Database.GetConnectionString()!)
            .AddInterceptors(counter).Options;
        await using var db = new GameDbContext(options);
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var service = new BattleService(db, new UserService(db, progression, skills), ConsumableTestFactory.Create(),
            skills, RewardTestFactory.CreateService(db, progression));

        var (manual, manualError) = await service.SyncAsync(test.Room.Id, test.Token);
        Assert.Null(manualError);
        Assert.Equal(RoomStatus.NotStarted, manual!.RoomStatus);
        Assert.Equal(1000, manual.MonsterHp);
        Assert.Single(counter.Queries);
        AssertPartyPredicate(counter.Queries[0], partyIds);

        foreach (var characterId in partyIds)
            db.CharacterBattleMilestones.Add(new CharacterBattleMilestone { CharacterId = characterId,
                Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1 });
        await db.SaveChangesAsync();
        counter.Queries.Clear();
        var (automatic, automaticError) = await service.SyncAsync(test.Room.Id, test.Token);
        Assert.Null(automaticError);
        Assert.Equal(RoomStatus.Cooldown, automatic!.RoomStatus);
        Assert.Equal(1000 - partySize * 20, automatic.MonsterHp);
        Assert.NotEmpty(counter.Queries);
        Assert.All(counter.Queries, query => AssertPartyPredicate(query, partyIds));
    }

    private static void AssertPartyPredicate(PartyMilestoneQuery query, IReadOnlyCollection<int> partyIds)
    {
        // EF 10 expands collection parameters and reduces a single member to '='.
        // Verify the actual SQL scope, independent of those two equivalent shapes.
        var predicate = Regex.Match(query.Sql,
            "WHERE[\\s\\S]*\"CharacterId\"\\s*(?:IN\\s*\\((?<parameters>[^)]+)\\)|=\\s*(?<parameters>@\\w+))",
            RegexOptions.IgnoreCase);
        Assert.True(predicate.Success, $"Expected a character-scoped predicate: {query.Sql}");
        var names = Regex.Matches(predicate.Groups["parameters"].Value, "@\\w+")
            .Select(match => match.Value).Distinct().ToArray();
        Assert.NotEmpty(names);
        var values = names.Select(name => int.Parse(Assert.Single(query.Parameters, item => item.Name == name).Value,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(partyIds.Order(), values.Distinct().Order());
    }

    private sealed record PartyMilestoneQuery(string Sql, List<(string Name, string Value)> Parameters);
    private sealed class PartyMilestoneQueryCounter : DbCommandInterceptor
    {
        public List<PartyMilestoneQuery> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"CharacterBattleMilestones\"", StringComparison.Ordinal))
                Queries.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                    .Select(parameter => (parameter.ParameterName, parameter.Value?.ToString() ?? "")).ToList()));
            return ValueTask.FromResult(result);
        }
    }
}
