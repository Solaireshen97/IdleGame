using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class ParallelPlantingMigrationTests
{
    [Fact]
    public async Task UpgradeStopsLegacyWorkPreservesInventoryAndBattleAndCreatesIndependentPlots()
    {
        var path = Path.Combine(Path.GetTempPath(), $"planting-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260927050000_AddManualSkillTargets");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO CharacterItemStacks (CharacterId, ItemCode, Quantity, Version) VALUES (1, 'peacebloom', 47, 0);
                    INSERT INTO CharacterActivities (CharacterId, Kind, SourceId, StartedAtUtc) VALUES
                        (1, 'Gathering', 1, '2026-09-27 00:00:00'), (2, 'Production', 1, '2026-09-27 00:00:00'),
                        (3, 'Battle', 9, '2026-09-27 00:00:00');
                    INSERT INTO GatheringTasks (Id, UserId, CharacterId, PointCode, MaterialCode, CycleSeconds,
                        OutputQuantity, Status, StartedAtUtc, EndsAtUtc, NextCycleAtUtc, CompletedCycles, TotalQuantity,
                        Version, IsRare, ExtraYieldChancePercent, ExtraYieldQuantity, RareBonusChancePercent, BonusQuantity)
                    VALUES (1, 1, 1, 'elwynn-peacebloom', 'peacebloom', 20, 1, 'Running', '2026-09-27 00:00:00',
                        '2026-09-27 12:00:00', '2026-09-27 00:00:20', 3, 3, 0, 0, 0, 0, 0, 0);
                    INSERT INTO ProductionTasks (Id, UserId, CharacterId, RecipeCode, OutputCode, OutputQuantity,
                        IngredientsJson, CycleSeconds, Status, StartedAtUtc, EndsAtUtc, NextCycleAtUtc,
                        CompletedCycles, TotalQuantity, Version, ExtraYieldChancePercent, ExtraYieldQuantity,
                        IngredientSaveChancePercent, SavedIngredientQuantity)
                    VALUES (1, 1, 2, 'minor-healing-potion', 'minor-healing-potion', 1, '[]', 10, 'Running',
                        '2026-09-27 00:00:00', '2026-09-27 12:00:00', '2026-09-27 00:00:10', 2, 2, 0, 0, 0, 0, 0);
                    """);
            }
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260927060000_AddParallelPlanting");
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Equal(47, (await db.CharacterItemStacks.SingleAsync()).Quantity);
                Assert.Equal("Stopped", (await db.GatheringTasks.SingleAsync()).Status);
                Assert.Equal(("Stopped", 2), ((await db.ProductionTasks.SingleAsync()).Status, (await db.ProductionTasks.SingleAsync()).TotalQuantity));
                Assert.Equal("Battle", (await db.CharacterActivities.SingleAsync()).Kind);
                db.CharacterGardenPlots.Add(new CharacterGardenPlot { CharacterId = 3, PlotIndex = 0 });
                db.LogisticsRequests.Add(new LogisticsRequest { CharacterId = 3, RequestId = "migration-check", Kind = "Plant", Fingerprint = "test", CompletedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
                Assert.Single(await db.CharacterGardenPlots.ToListAsync());
                await db.Database.MigrateAsync();
                Assert.Single(await db.LogisticsRequests.ToListAsync());
            }
        }
        finally { File.Delete(path); }
    }
}
