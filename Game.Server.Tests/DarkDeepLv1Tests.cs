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
    private static MonsterCombatCatalog DarkLv1Catalog() => new(Options.Create(new ConfigurationBuilder()
        .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build()
        .GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));

    [Fact]
    public async Task DarkLv1BossUsesLocalClockAndResolvesClashingSkillsAcrossReloads()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Room.RoundNumber = 60;
        test.Monster.CombatProfileCode = "dark-deep-lv1-monster-5";
        await test.Db.SaveChangesAsync();
        var catalog = DarkLv1Catalog();
        const string fang = "dark-deep-lv1-matriarch-fang", mist = "dark-deep-lv1-matriarch-mist";
        string?[] expected = [null, fang, null, mist, null, fang, null, null, null, mist, fang, null, null, null, fang, mist];
        for (var local = 0; local < expected.Length; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
            room.RoundNumber = 60 + local;
            var party = new List<BattleParticipant> { new(await db.RoomSlots.SingleAsync(), await db.Characters.SingleAsync()) };
            var service = new MonsterCombatService(db, catalog);
            var intent = await service.EnsureIntentAsync(room, monster);
            Assert.Equal(expected[local], intent.SkillCode);
            Assert.Same(intent, await service.EnsureIntentAsync(room, monster));
            await service.ExecuteIntentAsync(room, monster, party, new Dictionary<int, ElementType>(), []);
            await service.Statuses.ResolveEndOfRoundAsync(room, monster, party, []);
            await db.SaveChangesAsync();
            var phase = await db.BattleMonsterPhaseStates.SingleAsync();
            Assert.Equal(60, phase.EncounterStartRound);
            Assert.False(phase.IsActive);
            Assert.Equal(0, phase.ActivationCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkLv1PoisonTicksTwiceFromSnapshotOrCanBeCleansed(bool cleanse)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 100);
        test.Character.MaxHp = test.Character.Hp = 1000;
        test.Room.RoundNumber = 59;
        test.Monster.CombatProfileCode = "dark-deep-lv1-monster-2";
        var catalog = DarkLv1Catalog(); var service = new MonsterCombatService(test.Db, catalog);
        List<BattleParticipant> party = [new(await test.Db.RoomSlots.SingleAsync(), test.Character)];
        await service.EnsureIntentAsync(test.Room, test.Monster);
        test.Room.RoundNumber = 60;
        await service.ExecuteIntentAsync(test.Room, test.Monster, party, new Dictionary<int, ElementType>(), []);
        await test.Db.SaveChangesAsync();
        var poison = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal(("dark-deep-lv1-poison", 1, 15, 60, 62),
            (poison.EffectCode, poison.Stacks, poison.PerTickValue, poison.AppliedRound, poison.ExpiresAfterRound));
        var before = test.Character.Hp;
        await service.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, party, []);
        Assert.Equal(before, test.Character.Hp); // Application is not an extra immediate poison tick.
        test.Monster.Attack = 400;
        await test.Db.SaveChangesAsync();
        for (var round = 61; round <= 63; round++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
            var character = await db.Characters.SingleAsync();
            room.RoundNumber = round;
            var fresh = new MonsterCombatService(db, catalog);
            if (cleanse && round == 61)
            {
                var removed = await fresh.RemoveFirstStatusAsync(room, "Character", [character.Id], false);
                Assert.Equal("dark-deep-lv1-poison", removed!.Code);
            }
            await fresh.Statuses.ResolveEndOfRoundAsync(room, monster,
                [new(await db.RoomSlots.SingleAsync(), character)], []);
            Assert.Equal(before - (cleanse ? 0 : 15 * Math.Min(2, round - 60)), character.Hp);
            await db.SaveChangesAsync();
        }
        await using var final = test.CreateDbContext();
        Assert.Empty(await final.BattleStatusEffects.ToListAsync());
    }

    [Fact]
    public async Task DarkLv1RefreshKeepsStrongerSnapshotWithoutStackingOrDelayingTick()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Character.MaxHp = test.Character.Hp = 1000;
        var statuses = new BattleStatusService(test.Db, DarkLv1Catalog().Statuses);
        test.Room.RoundNumber = 60;
        await statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "dark-deep-lv1-poison", 2, [], "", perTickValue: 20);
        test.Room.RoundNumber = 61;
        await statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "dark-deep-lv1-poison", 2, [], "", perTickValue: 10);
        var poison = Assert.Single(test.Db.BattleStatusEffects.Local);
        Assert.Equal((1, 20, 60, 63), (poison.Stacks, poison.PerTickValue, poison.AppliedRound, poison.ExpiresAfterRound));
        await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster,
            [new(await test.Db.RoomSlots.SingleAsync(), test.Character)], []);
        Assert.Equal(980, test.Character.Hp);
        await statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "dark-deep-lv1-poison", 2, [], "", perTickValue: 30);
        Assert.Equal((1, 30, 60), (poison.Stacks, poison.PerTickValue, poison.AppliedRound));
    }

    [Fact]
    public async Task DarkLv1InterruptedFangAppliesNoPoisonAndConsumesCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Monster.CombatProfileCode = "dark-deep-lv1-monster-2";
        var service = new MonsterCombatService(test.Db, DarkLv1Catalog());
        await service.EnsureIntentAsync(test.Room, test.Monster);
        test.Room.RoundNumber++;
        Assert.True(await service.InterruptCurrentIntentAsync(test.Room, test.Monster));
        await service.ExecuteIntentAsync(test.Room, test.Monster,
            [new(await test.Db.RoomSlots.SingleAsync(), test.Character)], new Dictionary<int, ElementType>(), []);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
        Assert.Equal(5, (await test.Db.BattleMonsterSkillCooldowns.SingleAsync()).ReadyAtRound);
    }
}
