using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace Game.Server.Tests;

public sealed class ProductionDueIndexTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DueIndexMigrationKeepsTasksAndSupportsActualScannerPredicate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-production-index-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            await using var db = new GameDbContext(options);
            await db.GetService<IMigrator>().MigrateAsync("20260930040000_AddEconomicRequestResults");
            db.ProductionTasks.AddRange(
                Task(1, "Running", now.AddMinutes(-1), now.AddMinutes(10)),
                Task(2, "Running", now.AddMinutes(10), now),
                Task(3, "Running", now.AddMinutes(10), now.AddMinutes(20)),
                Task(4, "Completed", now.AddDays(-1), now.AddDays(-1)),
                Task(5, "Running", now.AddMinutes(-1), now.AddMinutes(-1)),
                Task(6, "Running", now, now.AddMinutes(10)));
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(6, await db.ProductionTasks.CountAsync());
            Assert.Equal(Enumerable.Range(1, 6), await db.ProductionTasks.OrderBy(task => task.Id)
                .Select(task => task.CompletedCycles).ToListAsync());
            var due = db.ProductionTasks.AsNoTracking().Where(task => task.Status == "Running" &&
                (task.NextCycleAtUtc <= now || task.EndsAtUtc <= now)).Select(task => task.Id);
            Assert.Equal(new[] { 1, 2, 5, 6 }, (await due.ToListAsync()).Order());
            var plan = await Explain(db, due.ToQueryString());
            output.WriteLine("Scanner query plan:\n" + plan);
            Assert.Contains("IX_ProductionTasks_Status_NextCycleAtUtc", plan);
            Assert.Contains("IX_ProductionTasks_Status_EndsAtUtc", plan);
            Assert.Contains("NextCycleAtUtc<?", plan);
            Assert.Contains("EndsAtUtc<?", plan);
            Assert.DoesNotContain("SCAN p", plan, StringComparison.OrdinalIgnoreCase);
            var nextPlan = await Explain(db, db.ProductionTasks.Where(task => task.Status == "Running" && task.NextCycleAtUtc <= now)
                .Select(task => task.Id).ToQueryString());
            Assert.Contains("IX_ProductionTasks_Status_NextCycleAtUtc", nextPlan);
            var endPlan = await Explain(db, db.ProductionTasks.Where(task => task.Status == "Running" && task.EndsAtUtc <= now)
                .Select(task => task.Id).ToQueryString());
            Assert.Contains("IX_ProductionTasks_Status_EndsAtUtc", endPlan);
        }
        finally { File.Delete(path); }
    }

    private static ProductionTask Task(int id, string status, DateTime next, DateTime end) => new()
    {
        Id = id, CharacterId = id, UserId = 1, RecipeCode = "recipe", OutputCode = "output",
        OutputQuantity = 1, CycleSeconds = 10, IngredientsJson = "[]", Status = status,
        StartedAtUtc = next.AddMinutes(-10), NextCycleAtUtc = next, EndsAtUtc = end, CompletedCycles = id,
        TotalQuantity = id, RequestId = $"existing-{id}"
    };

    private static async Task<string> Explain(GameDbContext db, string queryString)
    {
        // ToQueryString emits SQLite CLI parameter directives. Preserve their actual
        // generated values when passing the scanner's exact SQL to EXPLAIN QUERY PLAN.
        var lines = queryString.Split('\n');
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        foreach (var line in lines.Where(line => line.StartsWith(".param set ", StringComparison.Ordinal)))
        {
            var parts = line[11..].Trim().Split(' ', 2);
            var parameter = command.CreateParameter();
            parameter.ParameterName = parts[0];
            parameter.Value = parts[1].Trim().Trim('\'');
            command.Parameters.Add(parameter);
        }
        command.CommandText = "EXPLAIN QUERY PLAN " + string.Join('\n', lines.Where(line => !line.StartsWith(".param", StringComparison.Ordinal)));
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return string.Join("\n", details);
    }
}
