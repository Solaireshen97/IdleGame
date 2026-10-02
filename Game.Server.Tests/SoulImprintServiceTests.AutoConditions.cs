using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class SoulImprintServiceTests
{
    public static IEnumerable<object[]> AutoConditions() => SkillAutoRules.Conditions.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AutoConditions))]
    public async Task ConditionsPersistPerInstanceAndSurviveReload(string condition)
    {
        await using var test = await SoulImprintTestContext.CreateAsync();
        var soul = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core" };
        var other = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core" };
        test.Db.AddRange(soul, other);
        await test.Db.SaveChangesAsync();
        var (response, error) = await test.Service.SetAutoAsync(test.Token, 1, soul.Id,
            new() { AutoUseEnabled = true, AutoConditionOverride = condition, AutoHpThresholdPercent = 37 });
        Assert.Null(error);
        var item = response!.SoulImprints.Single(s => s.Id == soul.Id);
        Assert.Equal((true, condition, condition, 37),
            (item.AutoUseEnabled, item.AutoConditionOverride, item.AutoCondition, item.AutoHpThresholdPercent));
        test.Db.ChangeTracker.Clear();
        var restored = await test.Db.CharacterSoulImprints.SingleAsync(s => s.Id == soul.Id);
        Assert.Equal((true, condition, 37), (restored.AutoUseEnabled, restored.AutoConditionOverride, restored.AutoHpThresholdPercent));
        Assert.Null((await test.Db.CharacterSoulImprints.SingleAsync(s => s.Id == other.Id)).AutoConditionOverride);
    }

    [Fact]
    public async Task LegacyTogglePreservesConditionAndExplicitResetRestoresDefault()
    {
        await using var test = await SoulImprintTestContext.CreateAsync();
        var soul = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core",
            AutoConditionOverride = "SelfHpBelowThreshold", AutoHpThresholdPercent = 45 };
        test.Db.Add(soul); await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.SetAutoAsync(test.Token, 1, soul.Id, new() { AutoUseEnabled = true })).Error);
        Assert.Equal(("SelfHpBelowThreshold", 45), (soul.AutoConditionOverride, soul.AutoHpThresholdPercent));
        var (response, error) = await test.Service.SetAutoAsync(test.Token, 1, soul.Id,
            new() { AutoUseEnabled = true, AutoConditionOverride = null, AutoHpThresholdPercent = 45 });
        Assert.Null(error);
        var item = Assert.Single(response!.SoulImprints);
        Assert.Null(item.AutoConditionOverride);
        Assert.Equal("Always", item.AutoCondition);
        Assert.Equal(45, item.AutoHpThresholdPercent);
    }

    [Theory]
    [InlineData("Unknown", 70, "InvalidAutoCondition")]
    [InlineData("Always", 0, "InvalidHpThreshold")]
    [InlineData("Always", 101, "InvalidHpThreshold")]
    public async Task InvalidConditionsDoNotChangeSoul(string condition, int threshold, string expectedError)
    {
        await using var test = await SoulImprintTestContext.CreateAsync();
        var soul = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core" };
        test.Db.Add(soul); await test.Db.SaveChangesAsync();
        Assert.Equal(expectedError, (await test.Service.SetAutoAsync(test.Token, 1, soul.Id,
            new() { AutoUseEnabled = true, AutoConditionOverride = condition, AutoHpThresholdPercent = threshold })).Error);
        Assert.False(soul.AutoUseEnabled);
        Assert.Null(soul.AutoConditionOverride);
        Assert.Equal(70, soul.AutoHpThresholdPercent);
    }
}
