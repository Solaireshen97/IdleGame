using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterLifecycleTests
{
    [Fact]
    public async Task ConcurrentDeletesCannotRemoveTheLastCharacter()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var ready = new Barrier(2);
        var results = await Task.WhenAll(new[] { 1, 2 }.Select(id => Task.Run(async () =>
        {
            await using var db = fixture.Open();
            var user = await db.Users.SingleAsync();
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(15)));
            return await new CharacterLifecycleService(db, SkillTestFactory.Create(), null).DeleteAsync(user, id);
        })));

        Assert.Single(results, result => result.Success);
        Assert.Contains(results, result => result.Error is "CannotDeleteLastCharacter" or "ConcurrencyConflict");
        await using var verify = fixture.Open();
        var survivor = await verify.Characters.SingleAsync();
        Assert.Equal(survivor.Id, (await verify.Users.SingleAsync()).ActiveCharacterId);
    }

    [Fact]
    public async Task StaleSelectionCannotOverwriteACommittedAccountChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var stale = fixture.Open();
        var users = fixture.Users(stale);
        Assert.Null((await users.GetCurrentUserEntityAsync("token")).Error);
        await using (var current = fixture.Open())
            Assert.Null((await fixture.Users(current).SelectCurrentCharacterAsync("token", 2)).Error);

        var rejected = await users.SelectCurrentCharacterAsync("token", 1);
        Assert.Equal("ConcurrencyConflict", rejected.Error);
        await using var verify = fixture.Open();
        Assert.Equal(2, (await verify.Users.SingleAsync()).ActiveCharacterId);
    }

    [Fact]
    public async Task StaleDeletionRollsBackAllCharacterData()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var stale = fixture.Open();
        var users = fixture.Users(stale);
        await users.GetCurrentUserEntityAsync("token");
        await using (var current = fixture.Open())
            Assert.Null((await fixture.Users(current).SelectCurrentCharacterAsync("token", 2)).Error);

        Assert.Equal("ConcurrencyConflict", (await users.DeleteCurrentCharacterAsync("token", 1)).Error);
        await using var verify = fixture.Open();
        Assert.Equal(2, await verify.Characters.CountAsync());
        Assert.Equal(2, await verify.CharacterFirstHuntWeaponClaims.CountAsync());
        Assert.Equal(2, await verify.BattleConsumableBuffs.CountAsync());
        Assert.Equal(2, (await verify.Users.SingleAsync()).ActiveCharacterId);
    }

    [Fact]
    public async Task DeletionCleansOwnedClaimsAndBuffsAndRetainsOtherCharactersData()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Open())
            Assert.True((await fixture.Users(db).DeleteCurrentCharacterAsync("token", 1)).Success);
        await using var verify = fixture.Open();
        Assert.Equal(2, (await verify.Characters.SingleAsync()).Id);
        Assert.Equal(2, (await verify.CharacterFirstHuntWeaponClaims.SingleAsync()).CharacterId);
        Assert.Equal(2, (await verify.BattleConsumableBuffs.SingleAsync()).CharacterId);
        Assert.Equal(2, (await verify.Users.SingleAsync()).ActiveCharacterId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(999)]
    public async Task ReadingFallbackSelectionDoesNotSavePendingChanges(int? selected)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Open();
        var user = await db.Users.SingleAsync();
        user.ActiveCharacterId = selected;
        await db.SaveChangesAsync();
        var character = (await db.Characters.FindAsync(1))!;
        character.Gold = 1234; // Uncommitted work must not be flushed by authentication/selection reads.
        var users = fixture.Users(db);
        Assert.Equal(1, (await users.GetCurrentUserAsync("token")).Response!.ActiveCharacterId);
        Assert.Equal(1, (await users.GetCurrentCharacterAsync("token")).Response!.CharacterId);
        Assert.Equal(1, Assert.Single((await users.GetCurrentCharactersAsync("token")).Response!, c => c.IsCurrent).CharacterId);
        await using var verify = fixture.Open();
        Assert.Equal(selected, (await verify.Users.SingleAsync()).ActiveCharacterId);
        Assert.Equal(0, (await verify.Characters.FindAsync(1))!.Gold);
    }

    [Fact]
    public async Task StaleRoomCreationAfterProfessionChangeRollsBackRoomAndMonsters()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var stale = fixture.Open();
        await fixture.Users(stale).GetCurrentUserAndActiveCharacterAsync("token");
        await using (var current = fixture.Open())
        {
            var skills = SkillTestFactory.Create();
            var changed = await new CombatProfessionService(current, fixture.Users(current), skills,
                ProgressionTestFactory.Create()).SwitchAsync("token", 1,
                new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
            Assert.Null(changed.Error);
        }

        var created = await fixture.Rooms(stale).CreateRoomAsync("Slime", "token");
        Assert.Equal("ConcurrencyConflict", created.Error);
        await using var verify = fixture.Open();
        Assert.Empty(await verify.Rooms.ToListAsync());
        Assert.Empty(await verify.Monsters.ToListAsync());
        Assert.Empty(await verify.CharacterActivities.ToListAsync());
        Assert.Equal("cleric", (await verify.Characters.FindAsync(1))!.ProfessionCode);
    }

    [Fact]
    public async Task StaleAssignmentAfterProfessionChangeCannotAdmitTheCharacter()
    {
        await using var fixture = await Fixture.CreateAsync();
        int roomId;
        await using (var setup = fixture.Open())
            roomId = (await fixture.Rooms(setup).CreateRoomAsync("Slime", "token")).Detail!.RoomId;
        await using var stale = fixture.Open();
        await stale.Characters.FindAsync(2);
        await using (var current = fixture.Open())
        {
            var switched = await new CombatProfessionService(current, fixture.Users(current), SkillTestFactory.Create(),
                ProgressionTestFactory.Create()).SwitchAsync("token", 2,
                new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
            Assert.Null(switched.Error);
        }
        var assigned = await fixture.Rooms(stale).AssignSlotAsync(roomId,
            new AssignRoomSlotRequest { CharacterId = 2, SlotIndex = 2 }, "token");
        Assert.Equal("ConcurrencyConflict", assigned.Error);
        await using var verify = fixture.Open();
        Assert.False(await verify.RoomSlots.AnyAsync(slot => slot.CharacterId == 2));
        Assert.False(await verify.CharacterActivities.AnyAsync(activity => activity.CharacterId == 2));
    }

    [Fact]
    public async Task StaleGuestJoinAfterProfessionChangeCannotAdmitTheCharacter()
    {
        await using var fixture = await Fixture.CreateAsync();
        int roomId;
        await using (var setup = fixture.Open())
        {
            roomId = (await fixture.Rooms(setup).CreateRoomAsync(null, "Slime", "token", isPublic: true)).Detail!.RoomId;
            setup.Users.Add(new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 3 });
            setup.Characters.Add(new Character { Id = 3, UserId = 2, Name = "guest", ProfessionCode = "knight", Hp = 100, MaxHp = 100 });
            setup.UserLoginSessions.Add(new UserLoginSession { UserId = 2, Token = "guest-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await setup.SaveChangesAsync();
        }
        await using var stale = fixture.Open();
        await fixture.Users(stale).GetCurrentUserAndActiveCharacterAsync("guest-token");
        await using (var current = fixture.Open())
        {
            Assert.Null((await new CombatProfessionService(current, fixture.Users(current), SkillTestFactory.Create(),
                ProgressionTestFactory.Create()).SwitchAsync("guest-token", 3,
                new SwitchCombatProfessionRequest { ProfessionCode = "cleric" })).Error);
        }
        Assert.Equal("ConcurrencyConflict", (await fixture.Rooms(stale).JoinRoomAsync(roomId,
            new JoinRoomRequest { SlotIndex = 2 }, "guest-token")).Error);
        await using var verify = fixture.Open();
        Assert.False(await verify.RoomSlots.AnyAsync(slot => slot.CharacterId == 3));
        Assert.False(await verify.CharacterActivities.AnyAsync(activity => activity.CharacterId == 3));
    }

    [Fact]
    public async Task ProfessionChangeWithAnEarlierCharacterReadRechecksBattleAdmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var stale = fixture.Open();
        await stale.Characters.FindAsync(1);
        await using (var current = fixture.Open())
            Assert.Null((await fixture.Rooms(current).CreateRoomAsync("Slime", "token")).Error);
        var changed = await new CombatProfessionService(stale, fixture.Users(stale), SkillTestFactory.Create(),
            ProgressionTestFactory.Create()).SwitchAsync("token", 1,
            new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
        Assert.Equal("CharacterBusy", changed.Error);
        await using var verify = fixture.Open();
        Assert.Equal("knight", (await verify.Characters.FindAsync(1))!.ProfessionCode);
    }

    private sealed class Fixture(string path) : IAsyncDisposable
    {
        public GameDbContext Open() => new(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        public UserService Users(GameDbContext db) => new(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        public RoomService Rooms(GameDbContext db) => new(db, Users(db), ProgressionTestFactory.Create(),
            ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(db, ProgressionTestFactory.Create()));

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(), $"idlegame-lifecycle-{Guid.NewGuid():N}.db"));
            await using var db = fixture.Open();
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 });
            db.UserLoginSessions.Add(new UserLoginSession
            {
                UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1)
            });
            foreach (var id in new[] { 1, 2 })
            {
                db.Characters.Add(new Character { Id = id, UserId = 1, Name = $"hero-{id}", ProfessionCode = "knight", Hp = 100, MaxHp = 100, Attack = 20 });
                db.CharacterFirstHuntWeaponClaims.Add(new CharacterFirstHuntWeaponClaim { CharacterId = id, DungeonId = 1 });
                db.BattleConsumableBuffs.Add(new BattleConsumableBuff { CharacterId = id, RoomId = 99, SkillLevel = 1, ExpiresAfterRound = 3 });
            }
            await db.SaveChangesAsync();
            return fixture;
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
