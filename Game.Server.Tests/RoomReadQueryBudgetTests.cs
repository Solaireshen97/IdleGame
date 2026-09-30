using System.Data.Common;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class RoomServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task RoomReadSharesOneStatusSelectAcrossDisplayAndEveryOwnedSkill(int partySize)
    {
        await using var test = await RoomTestContext.CreateAsync();
        test.ActiveCharacter.ProfessionCode = "cleric";
        var (created, error) = await test.Service.CreateRoomAsync("Slime", test.Token);
        Assert.Null(error);
        for (var index = 2; index <= partySize; index++)
        {
            var character = await test.AddCharacterAsync($"Cleric {index}");
            character.ProfessionCode = "cleric";
            Assert.Null((await test.Service.AssignSlotAsync(created!.RoomId,
                new AssignRoomSlotRequest { CharacterId = character.Id, SlotIndex = index }, test.Token)).Error);
        }
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        var characterIds = await test.Db.RoomSlots.Where(slot => slot.RoomId == room.Id && slot.CharacterId.HasValue)
            .Select(slot => slot.CharacterId!.Value).ToArrayAsync();
        foreach (var characterId in characterIds)
        {
            test.Db.CharacterSkillSlots.AddRange(
                new CharacterSkillSlot { CharacterId = characterId, SlotIndex = 1, SkillCode = "cleric-purify" },
                new CharacterSkillSlot { CharacterId = characterId, SlotIndex = 2, SkillCode = "cleric-dispel" });
            test.Db.BattleStatusEffects.Add(new BattleStatusEffect { RoomId = room.Id, RunSequence = room.RunSequence,
                TargetType = "Character", TargetId = characterId, EffectCode = "poison", ExpiresAfterRound = 3,
                BoundTargetType = "Monster", BoundTargetId = room.MonsterId });
        }
        test.Db.BattleStatusEffects.Add(new BattleStatusEffect { RoomId = room.Id, RunSequence = room.RunSequence,
            TargetType = "Monster", TargetId = room.MonsterId, EffectCode = "resource", ExpiresAfterRound = 3 });
        await test.Db.SaveChangesAsync();

        var counter = new RoomReadQueryCounter();
        await using var db = QueryDb(test.Db, counter);
        var service = QueryRooms(db, monsterCombat: true);
        var detail = await service.GetRoomDetailAsync(room.Id, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        Assert.Equal(1, counter.SelectsFor("BattleStatusEffects"));
        Assert.Equal(1, counter.Commands.Count(command => command.Contains("SELECT \"m\".\"Id\", \"m\".\"Name\"", StringComparison.Ordinal)));
        Assert.All(detail.Slots.Where(slot => slot.CharacterId.HasValue), slot =>
        {
            var effect = Assert.Single(slot.StatusEffects);
            Assert.Equal(detail.MonsterName, effect.BoundTargetName);
            Assert.True(slot.Skills.Single(skill => skill.SkillCode == "cleric-purify").CanUse);
            Assert.True(slot.Skills.Single(skill => skill.SkillCode == "cleric-dispel").CanUse);
        });
        Assert.Single(detail.MonsterEffects);
        Assert.Empty(db.ChangeTracker.Entries<BattleStatusEffect>());
    }

    [Fact]
    public async Task LightweightRoomReadOmitsEveryRewardQueryAndDefaultStillIncludesRewards()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.RunSequence = 100;
        for (var sequence = 1; sequence <= 100; sequence++)
        {
            test.Db.RewardRuns.Add(new RewardRun { RoomId = room.Id, Sequence = sequence, Status = "Victory" });
            test.Db.RewardEntries.Add(new RewardEntry { RoomId = room.Id, Sequence = sequence, UserId = 1,
                CharacterId = test.ActiveCharacter.Id, Kind = "Gold", Code = "gold", Quantity = 2 });
        }
        await test.Db.SaveChangesAsync();
        var counter = new RoomReadQueryCounter();
        await using var db = QueryDb(test.Db, counter);
        var service = QueryRooms(db);

        var light = await service.GetRoomDetailAsync(room.Id, test.Token, includeRewardDetails: false);
        Assert.NotNull(light);
        Assert.Null(light.Rewards);
        Assert.Null(light.CumulativeRewards);
        Assert.Equal(0, counter.SelectsFor("RewardRuns"));
        Assert.Equal(0, counter.SelectsFor("RewardEntries"));
        Assert.DoesNotContain(counter.Commands, command => command.Contains("SELECT \"c\".\"Id\", \"c\".\"Name\"", StringComparison.Ordinal));

        var full = await service.GetRoomDetailAsync(room.Id, test.Token);
        Assert.Equal(200, full!.CumulativeRewards!.Gold);
        Assert.Equal(2, full.Rewards!.Gold);
        Assert.True(counter.SelectsFor("RewardEntries") > 0);

        counter.Commands.Clear();
        var independent = await service.GetRoomRewardsAsync(room.Id, test.Token);
        Assert.Equal(full.CumulativeRewards.Gold, independent!.CumulativeRewards!.Gold);
        Assert.Equal(full.Rewards.Gold, independent.Rewards!.Gold);
        Assert.Equal(0, counter.SelectsFor("BattleStatusEffects"));
        Assert.Equal(0, counter.SelectsFor("CharacterSkillSlots"));
        Assert.Equal(0, counter.SelectsFor("CharacterItemStacks"));
    }

    [Fact]
    public async Task IndependentRewardsRespectPrivateRoomAccessAndFilterToTheCurrentUser()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        await test.AddOtherActiveCharacterAsync();
        test.Db.RewardRuns.Add(new RewardRun { RoomId = created!.RoomId, Sequence = 1, Status = "Victory" });
        test.Db.RewardEntries.AddRange(
            new RewardEntry { RoomId = created.RoomId, Sequence = 1, UserId = 1, CharacterId = 1, Kind = "Gold", Code = "gold", Quantity = 10 },
            new RewardEntry { RoomId = created.RoomId, Sequence = 1, UserId = 2, CharacterId = 2, Kind = "Gold", Code = "gold", Quantity = 99 });
        await test.Db.SaveChangesAsync();
        var counter = new RoomReadQueryCounter();
        await using var db = QueryDb(test.Db, counter);
        var service = QueryRooms(db);
        Assert.Null(await service.GetRoomRewardsAsync(created.RoomId, "other-token"));
        Assert.Null(await service.GetRoomRewardsAsync(created.RoomId));
        Assert.Equal(0, counter.SelectsFor("RewardEntries"));
        var rewards = await service.GetRoomRewardsAsync(created.RoomId, test.Token);
        Assert.Equal(10, rewards!.CumulativeRewards!.Gold);
        Assert.Equal(10, rewards.Rewards!.Gold);
        Assert.Equal(1, Assert.Single(rewards.CumulativeRewards.Characters).CharacterId);
    }

    [Fact]
    public async Task DungeonProgressIsBatchedAndSinglePreviewDoesNotLoadOtherVisibleDungeons()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var depthOptions = new DungeonDepthOptions();
        test.Db.Dungeons.Add(new Dungeon { Id = 100, Code = "prerequisite", Name = "Ordinary", RegionCode = "region",
            DungeonKind = "Dungeon", MonsterName = "Ordinary", MonsterMaxHp = 100 });
        for (var id = 101; id <= 105; id++)
        {
            var code = $"depth-{id}";
            test.Db.Dungeons.Add(new Dungeon { Id = id, Code = code, Name = code, RegionCode = "region",
                MonsterName = code, MonsterMaxHp = 100, DungeonKind = "Dungeon" });
            depthOptions.Dungeons[code] = new DungeonDepthDefinitionOptions
                { PrerequisiteDungeonCode = "prerequisite", ChallengeFragmentCode = "fragment", MaximumDepth = 6 };
        }
        test.Db.UserDungeonClears.AddRange(new UserDungeonClear { UserId = 1, DungeonId = 100, HighestDepth = 1 },
            new UserDungeonClear { UserId = 1, DungeonId = 101, HighestDepth = 3 });
        test.Db.CharacterDungeonProgress.Add(new CharacterDungeonProgress { CharacterId = 1, DungeonId = 101, HighestDepth = 2 });
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone { CharacterId = 1,
            Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "depth-102", Count = 1 });
        await test.Db.SaveChangesAsync();
        var depths = new DungeonDepthCatalog(Options.Create(depthOptions));
        var counter = new RoomReadQueryCounter();
        await using (var db = QueryDb(test.Db, counter))
        {
            var list = await QueryRooms(db, depths: depths).GetDungeonsAsync(test.Token);
            Assert.Equal(6, list.Count);
            Assert.Equal(1, counter.SelectsFor("CharacterDungeonProgress"));
            Assert.Equal(1, counter.SelectsFor("UserDungeonClears"));
            Assert.Equal((2, 4), (list.Single(dungeon => dungeon.DungeonId == 101).CharacterHighestDepth,
                list.Single(dungeon => dungeon.DungeonId == 101).UnlockedDepth));
            Assert.Equal((1, 1), (list.Single(dungeon => dungeon.DungeonId == 102).CharacterHighestDepth,
                list.Single(dungeon => dungeon.DungeonId == 102).UnlockedDepth));
        }
        await using (var db = QueryDb(test.Db, new RoomReadQueryCounter()))
        {
            var preview = await QueryRooms(db, depths: depths).GetDungeonAsync(101, test.Token, 3);
            Assert.NotNull(preview);
            Assert.Equal(3, preview.DepthLevel);
            Assert.Equal(4, preview.UnlockedDepth);
            Assert.DoesNotContain(db.Dungeons.Local, dungeon => dungeon.Id is >= 102 and <= 105);
        }
    }

    private static GameDbContext QueryDb(GameDbContext source, RoomReadQueryCounter counter) =>
        new(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(source.Database.GetConnectionString()!)
            .AddInterceptors(counter).Options);

    private static RoomService QueryRooms(GameDbContext db, bool monsterCombat = false, DungeonDepthCatalog? depths = null)
    {
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.CreateResponses();
        var combat = monsterCombat ? new MonsterCombatService(db, new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects =
            [
                new() { Code = "poison", Name = "Poison", Description = "Debuff", EffectType = "DamageOverTime" },
                new() { Code = "resource", Name = "Resource", Description = "Buff", EffectType = "None", IsPositive = true }
            ]
        }))) : null;
        return new RoomService(db, new UserService(db, progression, skills), progression, ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(db, progression), monsterCombatService: combat, depthCatalog: depths);
    }

    private sealed class RoomReadQueryCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public int SelectsFor(string table) => Commands.Count(command => command.Contains($"\"{table}\"", StringComparison.Ordinal));
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
