using Game.Server.Configuration;
using System.Text.Json;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task SeededMultiRoundEncounterTraceSurvivesIndependentDatabaseAndServiceRecreationAcrossWaves()
    {
        var original = await RunTraceEncounterAsync(false);
        var repeated = await RunTraceEncounterAsync(false);
        var rebuilt = await RunTraceEncounterAsync(true);
        Assert.Equal(6, original.Count);
        Assert.Contains(original, trace => trace.Data.GetProperty("CurrentWaveNumber").GetInt32() == 2);
        Assert.Contains(original, trace => trace.Data.GetProperty("Statuses").GetArrayLength() > 0);
        Assert.Contains(original, trace => trace.Data.GetProperty("MonsterCooldowns").GetArrayLength() > 0);
        Assert.Equal(original.Select(trace => trace.Data.GetRawText()), repeated.Select(trace => trace.Data.GetRawText()));
        Assert.Equal(original.Select(trace => trace.Data.GetRawText()), rebuilt.Select(trace => trace.Data.GetRawText()));
        Assert.Equal(BattleRoundTrace.EncounterFingerprint(original), BattleRoundTrace.EncounterFingerprint(rebuilt));
        var fixturePath = TestRepository.File("Game.Server.Tests", "Fixtures", "BattleRoundTrace.seed7213.json");
        if (Environment.GetEnvironmentVariable("IDLEGAME_UPDATE_BATTLE_TRACE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true }));
        }
        var expected = JsonSerializer.Deserialize<List<BattleRoundTrace>>(await File.ReadAllTextAsync(fixturePath))!;
        Assert.Equal(expected.Count, original.Count);
        for (var round = 0; round < expected.Count; round++)
            Assert.Equal(JsonSerializer.Serialize(expected[round].Data), JsonSerializer.Serialize(original[round].Data));
        Assert.Equal(expected.Select(trace => trace.Fingerprint), original.Select(trace => trace.Fingerprint));
    }

    private static async Task<List<BattleRoundTrace>> RunTraceEncounterAsync(bool rebuild)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 4, monsterDefense: 0);
        var ally = await test.AddSlotAsync(2, "Ally", attack: 10);
        await test.EnableAutoForCharacterAsync(test.Character);
        await test.EnableAutoForCharacterAsync(ally);
        test.Room.IsOwnerAutoEnabled = true;
        test.Room.TotalWaveCount = 2;
        test.Monster.RoomId = test.Room.Id;
        test.Monster.Hp = test.Monster.MaxHp = 50;
        test.Monster.CombatProfileCode = "boss";
        test.Character.WeaponCriticalChancePercent = 50;
        test.Db.Monsters.Add(new Monster
        {
            RoomId = test.Room.Id, WaveNumber = 2, Position = 1, Name = "Second wave", Hp = 1000,
            MaxHp = 1000, Attack = 4, Defense = 0, CombatProfileCode = "boss"
        });
        await test.Db.SaveChangesAsync();
        var random = new Random(7213);
        var combatOptions = CreateUnifiedMonsterOptions(
        [
            new() { Type = "Damage", Target = "AllAlive", AttackPowerPercent = 100 },
            new() { Type = "ApplyStatus", Target = "AllAlive", StatusCode = "weakness", DurationRounds = 3 }
        ]);
        combatOptions.StatusEffects = [new() { Code = "weakness", Name = "Weakness", Description = "Less attack",
            EffectType = "AttackPercent", ValuePerStack = -10 }];
        var combatCatalog = new MonsterCombatCatalog(Options.Create(combatOptions));
        var skills = new SkillCatalog(Options.Create(new SkillOptions()));
        var traces = new List<BattleRoundTrace>();
        var db = test.CreateDbContext();
        BattleService? service = null;
        try
        {
            for (var step = 0; traces.Count < 6 && step < 20; step++)
            {
                if (rebuild && step > 0)
                {
                    await db.DisposeAsync();
                    db = test.CreateDbContext();
                    service = null;
                }
                var room = await db.Rooms.SingleAsync();
                if (room.NextRoundAvailableAtUtc.HasValue)
                {
                    room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddMinutes(-1);
                    await db.SaveChangesAsync();
                }
                if (service is null)
                {
                    var progression = ProgressionTestFactory.Create();
                    var rewards = RewardTestFactory.CreateService(db, progression);
                    var combat = new MonsterCombatService(db, combatCatalog, random);
                    service = new BattleService(db, new UserService(db, progression, skills), ConsumableTestFactory.Create(),
                        skills, rewards, new DungeonRunService(db, rewards, combat), combat, random: random);
                }
                var before = room.RoundNumber;
                var (result, error) = await service.SyncRoomAsync(room.Id);
                Assert.Null(error);
                Assert.NotNull(result);
                if (room.RoundNumber == before) continue;
                traces.Add(BattleRoundTrace.Capture(room, result, await UnifiedPartyAsync(db), await db.Monsters.ToListAsync(),
                    await db.BattleStatusEffects.ToListAsync(), await db.BattleSkillCooldowns.ToListAsync(),
                    await db.BattleMonsterSkillCooldowns.ToListAsync(), await db.BattleConsumableCooldowns.ToListAsync(),
                    await db.BattleHealingPotionStates.ToListAsync(), await db.BattleOperationPotionStates.ToListAsync(),
                    await db.BattleConsumableBuffs.ToListAsync()));
            }
        }
        finally { await db.DisposeAsync(); }
        return traces;
    }

    [Fact]
    public void TraceIgnoresStorageAndPresentationButRetainsCombatChanges()
    {
        BattleRoundTrace Capture(int offset, int damage = 8, bool reverse = false, int readyAt = 3, int stacks = 1,
            bool changeSource = false, int wave = 1, int consumableReadyAt = 2, int potionUses = 1,
            int buffLevel = 1, int operationAttack = 3)
        {
            var character = new Character { Id = offset + 1, Hp = 92, MaxHp = 100, Name = $"Actor {offset}" };
            var monster = new Monster { Id = offset + 2, Hp = 80, MaxHp = 100, WaveNumber = wave, Name = $"Enemy {offset}" };
            var room = new Room { Id = offset + 3, MonsterId = monster.Id, RoundNumber = 1, RunSequence = 1,
                Version = offset, CurrentWaveNumber = wave, TotalWaveCount = 2, Status = RoomStatus.Cooldown };
            var hits = new List<BattleEventResponse>
            {
                new() { Id = offset, RoomId = room.Id, MonsterId = monster.Id, Sequence = 1, RoundNumber = 1,
                    RunSequence = 1, SettlementVersion = offset, Label = $"Text {offset}", Kind = BattleEventKind.Damage,
                    Source = changeSource ? new("Character", character.Id) : new("Monster", monster.Id, Name: monster.Name),
                    Target = new("Character", character.Id),
                    CalculatedAmount = damage, ActualAmount = damage },
                new() { Id = offset + 1, RoomId = room.Id, MonsterId = monster.Id, Sequence = 2, RoundNumber = 1,
                    RunSequence = 1, Kind = BattleEventKind.Damage, Source = new("Character", character.Id),
                    Target = new("Monster", monster.Id), CalculatedAmount = 20, ActualAmount = 20 },
                new() { Id = offset + 2, RoomId = room.Id, MonsterId = monster.Id, Sequence = 3, RoundNumber = 1,
                    RunSequence = 1, Kind = BattleEventKind.Status, Target = new("Character", character.Id),
                    StatusChange = BattleStatusChange.Added, CountBefore = 0, CountAfter = stacks,
                    Status = new() { Code = "weakness", Name = $"Display {offset}", Description = $"Words {offset}",
                        Stacks = stacks, SourceActorType = "Monster", SourceActorId = monster.Id,
                        BoundTargetType = "Monster", BoundTargetId = monster.Id, BoundTargetName = monster.Name,
                        DurationText = $"Duration {offset}", CounterText = $"Counter {offset}" } }
            };
            if (reverse) hits.Reverse();
            return BattleRoundTrace.Capture(room, new BattleResult { Events = hits, ServerTimeUtc = DateTime.UtcNow,
                    Logs = [$"Presentation {offset}"] },
                [new(new RoomSlot { CharacterId = character.Id, SlotIndex = 1 }, character)], [monster],
                [new() { Id = offset, RoomId = room.Id, RunSequence = 1, TargetType = "Character", TargetId = character.Id,
                    EffectCode = "weakness", Stacks = stacks, ExpiresAfterRound = 3, SourceActorType = "Monster", SourceActorId = monster.Id }],
                [new() { Id = offset, RoomId = room.Id, CharacterId = character.Id, SkillCode = "strike", ReadyAtRound = readyAt }], [],
                [new() { Id = offset, RoomId = room.Id, CharacterId = character.Id, CooldownGroup = "Healing", ReadyAtRound = consumableReadyAt }],
                [new() { RoomId = room.Id, RunSequence = 1, CharacterId = character.Id, UsesUsed = potionUses, Version = offset }],
                [new() { RoomId = room.Id, RunSequence = 1, CharacterId = character.Id, ItemCode = "attack-potion", AttackPercent = operationAttack }],
                [new() { Id = offset, RoomId = room.Id, RunSequence = 1, CharacterId = character.Id,
                    ItemCode = "buff-potion", WeaponSkillCode = "power", SkillLevel = buffLevel, ExpiresAfterRound = 3 }]);
        }
        var baseline = Capture(0);
        Assert.Equal(baseline.Data.GetRawText(), Capture(100).Data.GetRawText());
        Assert.Equal(baseline.Fingerprint, Capture(100).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, damage: 9).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, reverse: true).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, readyAt: 4).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, stacks: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, changeSource: true).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, wave: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, consumableReadyAt: 3).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, potionUses: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, buffLevel: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Capture(0, operationAttack: 4).Fingerprint);
    }
}
