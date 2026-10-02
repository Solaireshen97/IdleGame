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
    private static MonsterCombatCatalog WindProductionCatalog() => new(Options.Create(new ConfigurationBuilder()
        .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build()
        .GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));

    [Fact]
    public async Task WindBossUsesLocalDiveSongTempestCadenceAcrossServiceReloads()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Room.RoundNumber = 60;
        test.Monster.CombatProfileCode = "wind-deep-lv1-monster-5";
        test.Monster.Element = ElementType.Wind;
        await test.Db.SaveChangesAsync();
        var catalog = WindProductionCatalog();
        const string dive = "wind-deep-lv1-matriarch-dive", song = "wind-deep-lv1-matriarch-song",
            tempest = "wind-deep-lv1-matriarch-tempest";
        string?[] expected = [null, dive, song, tempest, null, dive, null, tempest, null, dive, song, tempest];
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindRandomIntentExcludesDeadSlotsPersistsAndNeverRetargets(bool targetDies)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 10);
        var rear = await test.AddSlotAsync(2, "Rear");
        await test.AddSlotAsync(3, "Dead", hp: 0);
        test.Monster.CombatProfileCode = "wind-deep-lv1-monster-4";
        var catalog = WindProductionCatalog();
        // The first preview establishes local round one; the piercing skill starts at round two.
        await new MonsterCombatService(test.Db, catalog).EnsureIntentAsync(test.Room, test.Monster);
        test.Room.RoundNumber++;
        await test.Db.SaveChangesAsync();
        var random = new WindLastCandidateRandom();
        var service = new MonsterCombatService(test.Db, catalog, random);
        var intent = await service.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal("RandomAlive", intent.TargetType);
        Assert.Equal(rear.Id, intent.TargetCharacterId);
        Assert.Equal(1, random.Selections);
        var response = await service.GetIntentResponseAsync(test.Room, test.Monster);
        Assert.Contains("Rear", response!.TargetLabel);
        Assert.Equal(1, random.Selections);
        await test.Db.SaveChangesAsync();

        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        var freshRandom = new WindLastCandidateRandom();
        var freshService = new MonsterCombatService(db, catalog, freshRandom);
        var saved = await freshService.EnsureIntentAsync(room, monster);
        Assert.Equal(rear.Id, saved.TargetCharacterId);
        Assert.Equal(0, freshRandom.Selections);
        var characters = await db.Characters.ToDictionaryAsync(c => c.Id);
        if (targetDies) characters[rear.Id].Hp = 0;
        var slots = await db.RoomSlots.OrderBy(s => s.SlotIndex).ToListAsync();
        var frontHp = characters[test.Character.Id].Hp;
        await freshService.ExecuteIntentAsync(room, monster,
            slots.Select(s => new BattleParticipant(s, characters[s.CharacterId!.Value])).ToList(),
            new Dictionary<int, ElementType>(), []);
        Assert.Equal(frontHp, characters[test.Character.Id].Hp);
        if (targetDies) Assert.Equal(0, characters[rear.Id].Hp);
        else Assert.True(characters[rear.Id].Hp < 100);
        Assert.Equal(0, freshRandom.Selections);
    }

    private sealed class WindLastCandidateRandom : Random
    {
        public int Selections { get; private set; }
        public override int Next(int maxValue) { Selections++; return maxValue - 1; }
    }
}
