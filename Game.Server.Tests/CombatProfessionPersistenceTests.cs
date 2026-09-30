using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class CombatProfessionPersistenceTests
{
    [Fact]
    public async Task FreshContextsPreserveLegacySharedLoadoutAndRewardProgressAcrossSwitches()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Open())
        {
            var character = await db.Characters.SingleAsync();
            character.Level = 30;
            character.Experience = 0;
            db.CharacterCombatProfessions.Add(new CharacterCombatProfession
            {
                CharacterId = fixture.CharacterId, ProfessionCode = "cleric", Level = 10, Experience = 4,
                SkillLoadoutJson = JsonSerializer.Serialize(new[]
                {
                    new CombatSkillLoadoutSlot(1, "cleric-heal", true, "AllyHpBelowThreshold", 42),
                    new CombatSkillLoadoutSlot(2, "knight-hit", true, "Always", 55)
                })
            });
            await db.SaveChangesAsync();
        }
        await fixture.SwitchAsync("cleric");
        await using (var db = fixture.Open())
        {
            var character = await db.Characters.SingleAsync();
            Assert.Equal(("cleric", 10, 4), (character.ProfessionCode, character.Level, character.Experience));
            var slots = await db.CharacterSkillSlots.OrderBy(slot => slot.SlotIndex).ToListAsync();
            Assert.Equal("AllyHpBelowThreshold", slots[0].AutoConditionOverride);
            Assert.Equal(42, slots[0].AutoHpThresholdPercent);
            Assert.True(slots[0].AutoUseEnabled);
            Assert.Equal("knight-hit", slots[1].SkillCode);
            var oldVersion = character.Version;
            var gain = await new CombatProfessionProgressStore(db).AwardExperienceAsync(character, 25, fixture.Progression);
            Assert.Equal(new ProgressionGain(25, 2), gain);
            Assert.Equal(oldVersion + 1, character.Version);
            await db.SaveChangesAsync();
        }
        await fixture.SwitchAsync("knight");
        await using (var db = fixture.Open())
        {
            var character = await db.Characters.SingleAsync();
            Assert.Equal((30, 0), (character.Level, character.Experience));
            var slot = await db.CharacterSkillSlots.SingleAsync(slot => slot.SlotIndex == 1);
            Assert.Equal("knight-hit", slot.SkillCode);
            Assert.True(slot.AutoUseEnabled);
            Assert.Equal(37, slot.AutoHpThresholdPercent);
            var archived = await db.CharacterCombatProfessions.FindAsync(fixture.CharacterId, "cleric");
            Assert.Equal((12, 9), (archived!.Level, archived.Experience));
            using var snapshot = JsonDocument.Parse(archived.SkillLoadoutJson!);
            Assert.Equal(1, snapshot.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        await fixture.SwitchAsync("cleric");
        await using (var db = fixture.Open())
        {
            var character = await db.Characters.SingleAsync();
            Assert.Equal((12, 9), (character.Level, character.Experience));
            Assert.Equal("knight-hit", (await db.CharacterSkillSlots.SingleAsync(slot => slot.SlotIndex == 2)).SkillCode);
        }
    }

    [Fact]
    public async Task ExperienceOnlyRewardUpdatesBothProgressRecordsAndCharacterConcurrencyVersion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Open())
        {
            var dungeon = new Dungeon { Code = "slime-field", Name = "test" };
            db.Dungeons.Add(dungeon);
            await db.SaveChangesAsync();
            var room = new Room { DungeonId = dungeon.Id, OwnerUserId = 1, SlotCount = 1 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            db.RewardEntries.Add(new RewardEntry
            {
                RoomId = room.Id, Sequence = room.RunSequence, EventKey = "monster:test", UserId = 1,
                CharacterId = fixture.CharacterId, Kind = "Experience", Quantity = 25
            });
            await using var transaction = await db.Database.BeginTransactionAsync();
            await RewardTestFactory.CreateService(db, fixture.Progression).SettleAsync(room, false, DateTime.UtcNow, []);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var read = fixture.Open();
        var character = await read.Characters.SingleAsync();
        var progress = await read.CharacterCombatProfessions.SingleAsync();
        Assert.Equal((8, 4), (character.Level, character.Experience));
        Assert.Equal((character.Level, character.Experience), (progress.Level, progress.Experience));
        Assert.Equal(1, character.Version);
        Assert.Equal((76, 100, 20), (character.Hp, character.MaxHp, character.Attack));
        Assert.Equal("Defeat", (await read.RewardRuns.SingleAsync()).Status);
    }

    [Fact]
    public async Task UnknownSnapshotVersionRollsBackAllOutgoingProgressAndLoadoutChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string future = "{\"SchemaVersion\":2,\"Slots\":[]}";
        await using (var db = fixture.Open())
        {
            db.CharacterCombatProfessions.AddRange(
                new CharacterCombatProfession { CharacterId = fixture.CharacterId, ProfessionCode = "knight",
                    Level = 2, Experience = 3, SkillLoadoutJson = "outgoing-before" },
                new CharacterCombatProfession { CharacterId = fixture.CharacterId, ProfessionCode = "cleric",
                    Level = 10, Experience = 4, SkillLoadoutJson = future });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            var result = await fixture.Service(db).SwitchAsync("token", fixture.CharacterId,
                new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
            Assert.Null(result.Response);
            Assert.Equal("UnsupportedSkillLoadoutVersion", result.Error);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        await using var read = fixture.Open();
        var character = await read.Characters.SingleAsync();
        Assert.Equal(("knight", 5, 9, 0), (character.ProfessionCode, character.Level, character.Experience, character.Version));
        var outgoing = await read.CharacterCombatProfessions.FindAsync(fixture.CharacterId, "knight");
        Assert.Equal((2, 3, "outgoing-before"), (outgoing!.Level, outgoing.Experience, outgoing.SkillLoadoutJson));
        Assert.Equal(future, (await read.CharacterCombatProfessions.FindAsync(fixture.CharacterId, "cleric"))!.SkillLoadoutJson);
        Assert.Single(await read.CharacterSkillSlots.ToListAsync());
    }

    [Fact]
    public async Task FutureOutgoingSnapshotCannotBeOverwrittenByOlderServerOnSwitch()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string future = "{\"SchemaVersion\":2,\"Slots\":[],\"FutureMetadata\":true}";
        await using (var db = fixture.Open())
        {
            db.CharacterCombatProfessions.Add(new CharacterCombatProfession
            {
                CharacterId = fixture.CharacterId, ProfessionCode = "knight", Level = 5,
                Experience = 9, SkillLoadoutJson = future
            });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            var result = await fixture.Service(db).SwitchAsync("token", fixture.CharacterId,
                new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
            Assert.Equal("UnsupportedSkillLoadoutVersion", result.Error);
            Assert.Null(result.Response);
        }
        await using var read = fixture.Open();
        Assert.Equal(future, (await read.CharacterCombatProfessions.SingleAsync()).SkillLoadoutJson);
        Assert.Equal(("knight", 0), ((await read.Characters.SingleAsync()).ProfessionCode, (await read.Characters.SingleAsync()).Version));
    }

    [Fact]
    public async Task DamagedSnapshotUsesDefaultsAndOnlySuccessfulSwitchPersistsNewFormat()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Open())
        {
            db.CharacterCombatProfessions.Add(new CharacterCombatProfession
            {
                CharacterId = fixture.CharacterId, ProfessionCode = "cleric", SkillLoadoutJson = "{broken"
            });
            await db.SaveChangesAsync();
        }
        await fixture.SwitchAsync("cleric");
        await using (var db = fixture.Open())
        {
            var slot = await db.CharacterSkillSlots.SingleAsync(slot => slot.SlotIndex == 1);
            Assert.Equal("cleric-heal", slot.SkillCode);
            Assert.False(slot.AutoUseEnabled);
            Assert.Equal("{broken", (await db.CharacterCombatProfessions.FindAsync(fixture.CharacterId, "cleric"))!.SkillLoadoutJson);
        }
        await fixture.SwitchAsync("knight");
        await using var read = fixture.Open();
        using var snapshot = JsonDocument.Parse((await read.CharacterCombatProfessions.FindAsync(fixture.CharacterId, "cleric"))!.SkillLoadoutJson!);
        Assert.Equal(1, snapshot.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("cleric-heal", snapshot.RootElement.GetProperty("Slots")[0].GetProperty("SkillCode").GetString());
    }

    [Fact]
    public async Task StaleCharacterSwitchReturnsConflictWithoutSavingArchivedProgressOrSlots()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var stale = fixture.Open();
        await stale.Characters.SingleAsync();
        await using (var writer = fixture.Open())
            await writer.Database.ExecuteSqlRawAsync("UPDATE Characters SET Version = Version + 1");
        var result = await fixture.Service(stale).SwitchAsync("token", fixture.CharacterId,
            new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
        Assert.Equal("ConcurrencyConflict", result.Error);
        Assert.Null(result.Response);
        Assert.Empty(stale.ChangeTracker.Entries());
        await using var read = fixture.Open();
        Assert.Empty(await read.CharacterCombatProfessions.ToListAsync());
        Assert.Equal(("knight", 1), ((await read.Characters.SingleAsync()).ProfessionCode, (await read.Characters.SingleAsync()).Version));
        var slot = Assert.Single(await read.CharacterSkillSlots.ToListAsync());
        Assert.Equal(("knight-hit", 37), (slot.SkillCode, slot.AutoHpThresholdPercent));
    }

    [Fact]
    public async Task RealMigrationOfCharacterWithoutProfessionMirrorPreservesProgressAndSlotsOnRoundTrip()
    {
        await using var fixture = await Fixture.CreateAsync(migrateLegacy: true);
        await using (var db = fixture.Open())
        {
            Assert.Empty(await db.CharacterCombatProfessions.ToListAsync());
            var character = await db.Characters.SingleAsync();
            Assert.Equal(("knight", 5, 9), (character.ProfessionCode, character.Level, character.Experience));
        }
        await fixture.SwitchAsync("cleric");
        await fixture.SwitchAsync("knight");
        await using var read = fixture.Open();
        var restored = await read.Characters.SingleAsync();
        Assert.Equal(("knight", 5, 9), (restored.ProfessionCode, restored.Level, restored.Experience));
        var slot = await read.CharacterSkillSlots.SingleAsync(slot => slot.SlotIndex == 1);
        Assert.Equal(("knight-hit", true, "Always", 37),
            (slot.SkillCode, slot.AutoUseEnabled, slot.AutoConditionOverride, slot.AutoHpThresholdPercent));
        Assert.Equal(2, await read.CharacterCombatProfessions.CountAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"combat-profession-{Guid.NewGuid():N}.db");
        private DbContextOptions<GameDbContext> Options => new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").Options;
        public int CharacterId { get; private set; }
        public SkillCatalog Skills { get; } = CombatSkillLoadoutCodecTests.Catalog();
        public ProgressionService Progression { get; } = new(Microsoft.Extensions.Options.Options.Create(new ProgressionOptions
        {
            MaximumLevel = 30, ExperienceToNextLevel = Enumerable.Repeat(10, 29).ToList()
        }));
        public GameDbContext Open() => new(Options);
        public CombatProfessionService Service(GameDbContext db) => new(db, new UserService(db, Progression, Skills), Skills, Progression);
        public async Task SwitchAsync(string target)
        {
            await using var db = Open();
            var result = await Service(db).SwitchAsync("token", CharacterId,
                new SwitchCombatProfessionRequest { ProfessionCode = target });
            Assert.Null(result.Error);
            Assert.NotNull(result.Response);
        }
        public static async Task<Fixture> CreateAsync(bool migrateLegacy = false)
        {
            var fixture = new Fixture();
            try
            {
                await using (var db = fixture.Open())
                {
                    if (migrateLegacy)
                        await db.GetService<IMigrator>().MigrateAsync("20260927100000_SpecializeCombatConsumables");
                    else await db.Database.EnsureCreatedAsync();
                    var user = new User { UserName = "player", PasswordHash = "unused" };
                    db.Users.Add(user);
                    await db.SaveChangesAsync();
                    db.UserLoginSessions.Add(new UserLoginSession { UserId = user.Id, Token = "token",
                        CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddHours(1) });
                    var character = new Character { UserId = user.Id, Name = "hero", ProfessionCode = "knight",
                        Level = 5, Experience = 9, Hp = 76, MaxHp = 100, Attack = 20 };
                    db.Characters.Add(character);
                    await db.SaveChangesAsync();
                    fixture.CharacterId = character.Id;
                    db.CharacterSkillSlots.Add(new CharacterSkillSlot { CharacterId = character.Id, SlotIndex = 1,
                        SkillCode = "knight-hit", AutoUseEnabled = true, AutoConditionOverride = "Always", AutoHpThresholdPercent = 37 });
                    await db.SaveChangesAsync();
                }
                if (migrateLegacy)
                {
                    await using var db = fixture.Open();
                    await db.Database.MigrateAsync();
                }
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }
        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }
}
