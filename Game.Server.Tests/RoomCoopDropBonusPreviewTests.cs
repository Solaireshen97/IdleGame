using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class RoomServiceTests
{
    [Fact]
    public async Task CoopPreviewCountsOwnedAccountsOnceAndIncludesDownedMembersWithoutAwarding()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        await test.AddOtherActiveCharacterAsync();
        var duplicate = await test.AddCharacterAsync("Same account");
        var foreign = new Character { UserId = 3, Name = "Unmatched", Hp = 100, MaxHp = 100, Attack = 20 };
        test.Db.AddRange(new User { Id = 3, UserName = "unmatched", PasswordHash = "x" }, foreign);
        await test.Db.SaveChangesAsync();
        var downed = (await test.Db.Characters.FindAsync(2))!;
        downed.Hp = 0;
        var slots = await test.Db.RoomSlots.Where(slot => slot.RoomId == created!.RoomId).OrderBy(slot => slot.SlotIndex).ToListAsync();
        slots[1].CharacterId = duplicate.Id;
        slots[1].UserId = 1;
        slots[2].CharacterId = downed.Id;
        slots[2].UserId = 2;
        slots[2].HasParticipatedInRun = true;
        slots[3].CharacterId = foreign.Id;
        slots[3].UserId = 2; // Does not match the character's owner.
        slots[4].UserId = 3; // An account without an assigned character is not part of the preview.
        await test.Db.SaveChangesAsync();

        var detail = await CoopPreviewRooms(test).GetRoomDetailAsync(created!.RoomId, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        var preview = Assert.IsType<CoopDropBonusPreviewResponse>(detail.CoopDropBonus);
        Assert.Equal(2, preview.TeamPlayerCount);
        Assert.Equal(10m, preview.BonusPercent);
        Assert.Equal(10m, preview.PerAdditionalPlayerPercent);
        Assert.Equal(40m, preview.MaximumPercent);
        Assert.Equal(0, test.ActiveCharacter.Gold);
        Assert.Empty(await test.Db.RewardEvents.ToListAsync());
        Assert.Empty(await test.Db.RewardEntries.ToListAsync());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 20)]
    [InlineData(5, 40)]
    public async Task CoopPreviewUsesCurrentFormationAndConfiguredCap(int playerCount, int expectedBonus)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var slots = await test.Db.RoomSlots.Where(slot => slot.RoomId == created!.RoomId).OrderBy(slot => slot.SlotIndex).ToListAsync();
        for (var userId = 2; userId <= playerCount; userId++)
        {
            test.Db.AddRange(new User { Id = userId, UserName = $"user-{userId}", PasswordHash = "x" },
                new Character { Id = userId, UserId = userId, Name = $"Member {userId}", Hp = 100, MaxHp = 100 });
            slots[userId - 1].UserId = userId;
            slots[userId - 1].CharacterId = userId;
        }
        await test.Db.SaveChangesAsync();

        var detail = await CoopPreviewRooms(test).GetRoomDetailAsync(created!.RoomId, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        var preview = Assert.IsType<CoopDropBonusPreviewResponse>(detail.CoopDropBonus);
        Assert.Equal(playerCount, preview.TeamPlayerCount);
        Assert.Equal((decimal)expectedBonus, preview.BonusPercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 40)]
    [InlineData(10, 0)]
    public async Task CoopPreviewIsHiddenWhenConfiguredPolicyIsDisabled(int perUser, int maximum)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);

        var detail = await CoopPreviewRooms(test, perUser, maximum)
            .GetRoomDetailAsync(created!.RoomId, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        Assert.Null(detail.CoopDropBonus);
    }

    [Fact]
    public async Task CoopPreviewIgnoresAnAssignedCharacterThatNoLongerExists()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        test.Db.Users.Add(new User { Id = 2, UserName = "missing-member", PasswordHash = "x" });
        var slot = await test.Db.RoomSlots.SingleAsync(item => item.RoomId == created!.RoomId && item.SlotIndex == 2);
        slot.CharacterId = 999;
        slot.UserId = 2;
        await test.Db.SaveChangesAsync();

        var detail = await CoopPreviewRooms(test).GetRoomDetailAsync(created!.RoomId, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        var preview = Assert.IsType<CoopDropBonusPreviewResponse>(detail.CoopDropBonus);
        Assert.Equal(1, preview.TeamPlayerCount);
        Assert.Equal(0m, preview.BonusPercent);
    }

    [Fact]
    public async Task LegacyFrozenRoomWithoutCoopPolicyDoesNotPreviewTheNewCatalogPolicy()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        var monster = (await test.Db.Monsters.FindAsync(room.MonsterId))!;
        var definition = new DungeonRunDefinition
        {
            DungeonCode = "slime-field", DungeonKind = "Hunt", DepthLevel = room.DepthLevel,
            RewardEligibility = DungeonRewardEligibility.CurrentSlots, PartyHpPercentages = [100, 100, 100, 100, 100],
            Monsters = [DungeonMonsterDefinition.Capture(monster)]
        };
        var document = JsonNode.Parse(JsonSerializer.Serialize(definition))!;
        document["Rewards"]!.AsObject().Remove("CoopDropBonus");
        var json = document.ToJsonString();
        test.Db.DungeonRunRuleSnapshots.Add(new()
        {
            RoomId = room.Id, DefinitionJson = json,
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()
        });
        await test.Db.SaveChangesAsync();
        var catalog = CoopPreviewCatalog(10, 40);
        var rules = new DungeonRunRulesService(test.Db, new(Options.Create(new MonsterCombatOptions())), catalog,
            PartyScalingCatalog.Default, new(Options.Create(new DungeonDepthOptions())));

        var detail = await CoopPreviewRooms(test, runRules: rules).GetRoomDetailAsync(room.Id, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        Assert.Null(detail.CoopDropBonus);
        Assert.Equal(json, (await test.Db.DungeonRunRuleSnapshots.SingleAsync()).DefinitionJson);
    }

    private static RoomService CoopPreviewRooms(RoomTestContext test, decimal perUser = 10, decimal maximum = 40,
        DungeonRunRulesService? runRules = null)
    {
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        return new(test.Db, new UserService(test.Db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills,
            new RewardService(test.Db, CoopPreviewCatalog(perUser, maximum), progression, runRules: runRules), runRules: runRules);
    }

    private static RewardCatalog CoopPreviewCatalog(decimal perUser, decimal maximum) => new(Options.Create(new RewardOptions
    {
        CoopDropBonus = new() { PercentPerAdditionalUser = perUser, MaximumPercent = maximum }
    }), ConsumableTestFactory.Create(), new WeaponCatalog(Options.Create(new WeaponOptions
    {
        Items = [new() { Code = "test-weapon", Name = "Test", Attack = 1, MaxHp = 1,
            Skills = [new() { Code = "test-critical", Level = 1 }] }],
        Skills = [new() { Code = "test-critical", Name = "Critical", EffectType = WeaponSkillEffectType.CriticalChancePercent, PercentPerLevel = 1 }],
        StarterPacks = new() { ["knight"] = ["test-weapon"] }
    })));
}
