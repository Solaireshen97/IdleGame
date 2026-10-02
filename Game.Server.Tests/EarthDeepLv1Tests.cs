using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task EarthBossArmorKeepsItsLocalCadenceWithoutDelayingStrikeOrRockfallAcrossReloads()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Room.RoundNumber = 60;
        test.Monster.CombatProfileCode = "earth-deep-lv1-monster-5";
        test.Monster.Element = ElementType.Earth;
        await test.Db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var catalog = new MonsterCombatCatalog(Options.Create(configuration
            .GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        const string pick = "earth-deep-lv1-overseer-pick", armor = "earth-deep-lv1-overseer-armor",
            rockfall = "earth-deep-lv1-overseer-rockfall";
        string?[] expected = [null, pick, armor, rockfall, null, pick, null, rockfall,
            null, pick, armor, rockfall];
        for (var local = 0; local < expected.Length; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            var monster = await db.Monsters.SingleAsync();
            room.RoundNumber = 60 + local;
            var character = await db.Characters.SingleAsync();
            var slot = await db.RoomSlots.SingleAsync();
            var service = new MonsterCombatService(db, catalog);
            var intent = await service.EnsureIntentAsync(room, monster);
            Assert.Equal(expected[local], intent.SkillCode);
            Assert.Same(intent, await service.EnsureIntentAsync(room, monster));
            await service.ExecuteIntentAsync(room, monster, [new(slot, character)],
                new Dictionary<int, ElementType>(), []);
            await db.SaveChangesAsync();
            Assert.Equal(60, (await db.BattleMonsterPhaseStates.SingleAsync()).EncounterStartRound);
            Assert.False((await db.BattleMonsterPhaseStates.SingleAsync()).IsActive);
        }
    }
}
