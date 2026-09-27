using Game.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class PartyHpScalingMigrationTests
{
    [Fact]
    public async Task UpgradePreservesExistingHealthAndDowngradeRestoresBaseHealthWithoutRevivingDefeatedMonster()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260925010000_AddRoomVisibility");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Dungeons (Id,Code,Name,MonsterName,MonsterMaxHp,MonsterAttack,MonsterDefense,MonsterElement,
                SlotCount,SortOrder,RegionName,RegionCode,DungeonKind,Description,MinimumLevel,RecommendedLevel,IsVisible)
            VALUES (501,'test-hunt','Hunt','Target',10000,1,0,'Wind',5,501,'Test','test','Hunt','Test',1,1,1),
                   (502,'test-elite','Elite','Target',10000,1,0,'Wind',5,502,'Test','test','Elite','Test',1,1,1),
                   (503,'test-single-boss','Dungeon','Target',10000,1,0,'Wind',5,503,'Test','test','Dungeon','Test',1,1,1);
            INSERT INTO Monsters (Id,Name,Element,Hp,MaxHp,Attack,Defense,WaveNumber,Position,CombatProfileCode,RewardProfileCode,IsBoss)
            VALUES (501,'Target','Wind',1000,10000,1,0,1,1,'','test-hunt',0),
                   (502,'Dead','Wind',0,10000,1,0,1,1,'','test-elite',0);
            INSERT INTO Rooms (Id,DungeonId,MonsterId,OwnerUserId,SlotCount,Status,IsPreparationTimeoutEnabled,
                IsRepeatBattle,RoundNumber,RunSequence,Version,CurrentWaveNumber,TotalWaveCount)
            VALUES (501,501,501,1,5,2,1,0,1,1,0,1,1);
            """);
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var monster = (await db.Monsters.FindAsync(501))!;
        Assert.Equal((1000, 10000, 10000), (monster.Hp, monster.MaxHp, monster.BaseMaxHp));
        Assert.Equal(0, (await db.Monsters.FindAsync(502))!.Hp);
        Assert.Equal(1, (await db.Rooms.FindAsync(501))!.ScalingPartySize);
        Assert.Equal("hunt-hp", (await db.Dungeons.FindAsync(501))!.PartyScalingProfileCode);
        Assert.Equal("hunt-hp", (await db.Dungeons.FindAsync(502))!.PartyScalingProfileCode);
        Assert.Equal("fixed", (await db.Dungeons.FindAsync(503))!.PartyScalingProfileCode);
        monster.Hp = 4200;
        monster.MaxHp = 42000;
        await db.SaveChangesAsync();
        await migrator.MigrateAsync("20260925010000_AddRoomVisibility");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Hp,MaxHp FROM Monsters WHERE Id IN (501,502) ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((1000, 10000), (reader.GetInt32(0), reader.GetInt32(1)));
        Assert.True(await reader.ReadAsync());
        Assert.Equal((0, 10000), (reader.GetInt32(0), reader.GetInt32(1)));
    }
}
