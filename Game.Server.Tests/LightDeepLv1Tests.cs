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
    private static MonsterCombatCatalog LightProductionCatalog() => new(Options.Create(new ConfigurationBuilder()
        .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build()
        .GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));

    [Fact]
    public async Task LightBossKeepsLocalPierceWardNovaCadenceAcrossReloads()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Room.RoundNumber = 60;
        test.Monster.CombatProfileCode = "light-deep-lv1-monster-5";
        test.Monster.Element = ElementType.Light;
        await test.Db.SaveChangesAsync();
        var catalog = LightProductionCatalog();
        const string pierce = "light-deep-lv1-warden-pierce", ward = "light-deep-lv1-warden-ward",
            nova = "light-deep-lv1-warden-nova";
        string?[] expected = [null, pierce, ward, nova, null, pierce, null, nova, null, pierce, ward, nova];
        for (var local = 0; local < expected.Length; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            var monster = await db.Monsters.SingleAsync();
            room.RoundNumber = 60 + local;
            var service = new MonsterCombatService(db, catalog);
            var intent = await service.EnsureIntentAsync(room, monster);
            Assert.Equal(expected[local], intent.SkillCode);
            Assert.Same(intent, await service.EnsureIntentAsync(room, monster));
            await service.ExecuteIntentAsync(room, monster,
                [new(await db.RoomSlots.SingleAsync(), await db.Characters.SingleAsync())],
                new Dictionary<int, ElementType>(), []);
            await db.SaveChangesAsync();
            Assert.Equal(60, (await db.BattleMonsterPhaseStates.SingleAsync()).EncounterStartRound);
            Assert.False((await db.BattleMonsterPhaseStates.SingleAsync()).IsActive);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public async Task LightWardCoversTwoFullFollowingRoundsOrCanBeDispelled(int wave, bool dispel)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Monster.CombatProfileCode = $"light-deep-lv1-monster-{wave}";
        var catalog = LightProductionCatalog();
        var service = new MonsterCombatService(test.Db, catalog);
        for (var round = 0; round < 2; round++)
        {
            test.Room.RoundNumber = round;
            await service.ExecuteIntentAsync(test.Room, test.Monster,
                [new(await test.Db.RoomSlots.SingleAsync(), test.Character)], new Dictionary<int, ElementType>(), []);
        }
        test.Room.RoundNumber = 2;
        var intent = await service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal(wave == 2 ? "light-deep-lv1-construct-ward" : "light-deep-lv1-warden-ward", intent.SkillCode);
        await service.ExecuteIntentAsync(test.Room, test.Monster, [new(await test.Db.RoomSlots.SingleAsync(), test.Character)],
            new Dictionary<int, ElementType>(), []);
        await service.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, [], []);
        await test.Db.SaveChangesAsync();
        for (var round = 3; round <= 4; round++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            room.RoundNumber = round;
            var fresh = new MonsterCombatService(db, catalog);
            Assert.Equal(dispel && round == 4 ? 0m : 20m,
                await fresh.Statuses.ModifierAsync(room, "Monster", test.Monster.Id, "ReductionPercent"));
            if (dispel && round == 3)
            {
                var removed = await fresh.RemoveFirstStatusAsync(room, "Monster", [test.Monster.Id], true);
                Assert.Equal("light-deep-lv1-ward", removed!.Code);
                Assert.Equal(0m, await fresh.Statuses.ModifierAsync(room, "Monster", test.Monster.Id, "ReductionPercent"));
            }
            await fresh.Statuses.ResolveEndOfRoundAsync(room, await db.Monsters.SingleAsync(), [], []);
            await db.SaveChangesAsync();
        }
        await using var finalDb = test.CreateDbContext();
        Assert.Empty(await finalDb.BattleStatusEffects.ToListAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task LightInterruptedWardNeverAppliesReductionAndConsumesCooldown(int wave)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Monster.CombatProfileCode = $"light-deep-lv1-monster-{wave}";
        var service = new MonsterCombatService(test.Db, LightProductionCatalog());
        for (var round = 0; round < 2; round++)
        {
            test.Room.RoundNumber = round;
            await service.ExecuteIntentAsync(test.Room, test.Monster,
                [new(await test.Db.RoomSlots.SingleAsync(), test.Character)], new Dictionary<int, ElementType>(), []);
        }
        test.Room.RoundNumber = 2;
        var intent = await service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal(wave == 2 ? "light-deep-lv1-construct-ward" : "light-deep-lv1-warden-ward", intent.SkillCode);
        Assert.True(await service.InterruptCurrentIntentAsync(test.Room, test.Monster));
        await service.ExecuteIntentAsync(test.Room, test.Monster, [new(await test.Db.RoomSlots.SingleAsync(), test.Character)],
            new Dictionary<int, ElementType>(), []);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
        Assert.Equal(10, (await test.Db.BattleMonsterSkillCooldowns.SingleAsync(c => c.SkillCode == intent.SkillCode)).ReadyAtRound);
    }

    [Theory]
    [InlineData(1, "light-deep-lv1-wraith-bolt")]
    [InlineData(4, "light-deep-lv1-conduit-beam")]
    public async Task LightRandomPreviewPersistsAndDeadTargetDoesNotRetarget(int wave, string skill)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 10);
        var rear = await test.AddSlotAsync(2, "Rear");
        await test.AddSlotAsync(3, "Dead", hp: 0);
        test.Monster.CombatProfileCode = $"light-deep-lv1-monster-{wave}";
        var catalog = LightProductionCatalog();
        await new MonsterCombatService(test.Db, catalog).EnsureIntentAsync(test.Room, test.Monster);
        test.Room.RoundNumber++;
        await test.Db.SaveChangesAsync();
        var random = new LightLastCandidateRandom();
        var service = new MonsterCombatService(test.Db, catalog, random);
        var intent = await service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal(skill, intent.SkillCode);
        Assert.Equal(rear.Id, intent.TargetCharacterId);
        Assert.Contains("Rear", (await service.GetIntentResponseAsync(test.Room, test.Monster))!.TargetLabel);
        Assert.Equal(1, random.Selections);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var freshRandom = new LightLastCandidateRandom();
        var fresh = new MonsterCombatService(db, catalog, freshRandom);
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        Assert.Equal(rear.Id, (await fresh.EnsureIntentAsync(room, monster)).TargetCharacterId);
        var characters = await db.Characters.ToDictionaryAsync(c => c.Id);
        characters[rear.Id].Hp = 0;
        var slots = await db.RoomSlots.OrderBy(s => s.SlotIndex).ToListAsync();
        var frontHp = characters[test.Character.Id].Hp;
        await fresh.ExecuteIntentAsync(room, monster, slots.Select(s => new BattleParticipant(s, characters[s.CharacterId!.Value])).ToList(),
            new Dictionary<int, ElementType>(), []);
        Assert.Equal(frontHp, characters[test.Character.Id].Hp);
        Assert.Equal(0, characters[rear.Id].Hp);
        Assert.Equal(0, freshRandom.Selections);
    }

    private sealed class LightLastCandidateRandom : Random
    {
        public int Selections { get; private set; }
        public override int Next(int maxValue) { Selections++; return maxValue - 1; }
    }
}
