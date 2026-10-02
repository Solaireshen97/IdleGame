using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class RoomServiceTests
{
    [Fact]
    public async Task RoomPreviewReadsDirectDamageVarianceFromFrozenDefinition()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, rules) = await DirectDamagePreviewSnapshotAsync(test, 3m);

        var detail = await DirectDamagePreviewRooms(test, rules).GetRoomDetailAsync(room.Id, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        Assert.Equal(3m, detail.DirectDamageVariancePercent);
        Assert.Empty(await test.Db.RewardEvents.ToListAsync());
    }

    [Fact]
    public async Task LegacyFrozenRoomWithoutVarianceFieldPreviewsZeroAndPreservesItsJson()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, rules) = await DirectDamagePreviewSnapshotAsync(test, 3m, omitVariance: true);
        var original = (await test.Db.DungeonRunRuleSnapshots.SingleAsync()).DefinitionJson;

        var detail = await DirectDamagePreviewRooms(test, rules).GetRoomDetailAsync(room.Id, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        Assert.Equal(0m, detail.DirectDamageVariancePercent);
        Assert.Equal(original, (await test.Db.DungeonRunRuleSnapshots.SingleAsync()).DefinitionJson);
    }

    [Fact]
    public async Task RoomPreviewWithoutRunRulesKeepsCompatibilityVarianceZero()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await DirectDamagePreviewSnapshotAsync(test, 3m);

        var detail = await test.Service.GetRoomDetailAsync(room.Id, test.Token, includeRewardDetails: false);

        Assert.NotNull(detail);
        Assert.Equal(0m, detail.DirectDamageVariancePercent);
    }

    private static RoomService DirectDamagePreviewRooms(RoomTestContext test, DungeonRunRulesService rules)
    {
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        return new(test.Db, new UserService(test.Db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills,
            new RewardService(test.Db, RewardTestFactory.CreateCatalog(), progression, runRules: rules), runRules: rules);
    }

    private static async Task<(Room Room, DungeonRunRulesService Rules)> DirectDamagePreviewSnapshotAsync(
        RoomTestContext test, decimal percent, bool omitVariance = false)
    {
        var (created, error) = await test.Service.CreateRoomAsync("Slime", test.Token);
        Assert.Null(error);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        var monster = (await test.Db.Monsters.FindAsync(room.MonsterId))!;
        var definition = new DungeonRunDefinition
        {
            DungeonCode = "slime-field", DungeonKind = "Hunt", DepthLevel = room.DepthLevel,
            DirectDamageVariancePercent = percent,
            RewardEligibility = DungeonRewardEligibility.CurrentSlots, PartyHpPercentages = [100, 100, 100, 100, 100],
            Monsters = [DungeonMonsterDefinition.Capture(monster)]
        };
        var document = JsonNode.Parse(JsonSerializer.Serialize(definition))!;
        if (omitVariance) document.AsObject().Remove("DirectDamageVariancePercent");
        var json = document.ToJsonString();
        test.Db.DungeonRunRuleSnapshots.Add(new()
        {
            RoomId = room.Id, DefinitionJson = json,
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()
        });
        await test.Db.SaveChangesAsync();
        var rules = new DungeonRunRulesService(test.Db, new(Options.Create(new MonsterCombatOptions())),
            RewardTestFactory.CreateCatalog(), PartyScalingCatalog.Default, new(Options.Create(new DungeonDepthOptions())));
        return (room, rules);
    }
}
