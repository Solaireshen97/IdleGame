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
    public async Task WaterDeepBossStartsItsSkillClockOnArrivalAndKeepsCadenceAcrossReloads()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Room.RoundNumber = 60;
        test.Monster.CombatProfileCode = "water-deep-lv1-monster-5";
        test.Monster.Element = ElementType.Water;
        await test.Db.SaveChangesAsync();
        var catalog = WaterLv1Catalog();
        string?[] expected = [null, "water-deep-lv1-king-strike", null, "water-deep-lv1-king-tide",
            null, "water-deep-lv1-king-strike", null, "water-deep-lv1-king-tide"];
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
        await using var final = test.CreateDbContext();
        var finalRoom = await final.Rooms.SingleAsync();
        var finalMonster = await final.Monsters.SingleAsync();
        await new MonsterCombatService(final, catalog).ResetRoomStateAsync(finalRoom.Id);
        finalRoom.RunSequence++;
        finalRoom.RoundNumber = 0;
        Assert.Null((await new MonsterCombatService(final, catalog).EnsureIntentAsync(finalRoom, finalMonster)).SkillCode);
        await final.SaveChangesAsync();
        Assert.Equal(0, (await final.BattleMonsterPhaseStates.SingleAsync()).EncounterStartRound);
    }

    [Fact]
    public async Task WaterBasicColdUsesStrongerValueExpiresAfterTwoPlayerRoundsAndCanBeCleansed()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var catalog = WaterLv1Catalog();
        var statuses = new BattleStatusService(test.Db, catalog.Statuses);
        var room = test.Room;
        room.RoundNumber = 60;
        const string weak = "water-deep-lv1-chill-5", strong = "water-deep-lv1-chill-10";
        async Task Apply(string code) => await statuses.ApplyAsync(room, "Character", test.Character.Id,
            code, 2, [], test.Character.Name);
        await Apply(weak);
        await Apply(strong);
        await Apply(strong);
        await test.Db.SaveChangesAsync();
        Assert.Equal(-10m, await statuses.ModifierAsync(room, "Character", test.Character.Id, "AttackPercent"));
        Assert.Equal(1, (await test.Db.BattleStatusEffects.SingleAsync()).Stacks);
        for (var round = 61; round <= 62; round++)
        {
            room.RoundNumber = round;
            if (round == 61) await Apply(weak);
            Assert.Equal(-10m, await statuses.ModifierAsync(room, "Character", test.Character.Id, "AttackPercent"));
            await statuses.ResolveEndOfRoundAsync(room, test.Monster, [], []);
        }
        room.RoundNumber = 63;
        Assert.Equal(0m, await statuses.ModifierAsync(room, "Character", test.Character.Id, "AttackPercent"));
        await Apply(weak);
        var removed = await statuses.RemoveFirstAsync(room, "Character", [test.Character.Id], false);
        Assert.Equal(weak, removed?.Code);
        Assert.Equal(0m, await statuses.ModifierAsync(room, "Character", test.Character.Id, "AttackPercent"));
    }

    [Fact]
    public async Task ExistingFireProfilesKeepTheirGlobalInitialCooldownWithoutCreatingALocalClock()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.RoundNumber = 60;
        test.Monster.CombatProfileCode = "fire-deep-lv1-monster-5";
        var service = new MonsterCombatService(test.Db, WaterLv1Catalog());
        Assert.Equal("fire-deep-lv1-warlord-eruption", (await service.EnsureIntentAsync(test.Room, test.Monster)).SkillCode);
        Assert.Empty(test.Db.BattleMonsterPhaseStates.Local);
    }

    private static MonsterCombatCatalog WaterLv1Catalog()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        return new(Options.Create(configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
    }
}
