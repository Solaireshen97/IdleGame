using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class SkillTalentTreeTests
{
    [Fact]
    public async Task LevelFiveHealerCanLearnPurifyWithoutBuyingSilence()
    {
        await using var test = await FormalTreeContext.CreateAsync("acolyte", level: 5, points: 4);
        await PurchasePathAsync(test, "acolyte-prayer", "acolyte-echo", "acolyte-heal-training");

        var response = await PurchaseAvailableNodeAsync(test, "acolyte-purify-talent");

        Assert.Equal(0, response.TalentPoints);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "acolyte-purify");
        Assert.DoesNotContain(response.LearnedSkills, skill => skill.Code == "acolyte-silence");
        Assert.DoesNotContain(await test.Db.CharacterSkillTalents.ToListAsync(), node => node.NodeCode == "acolyte-silence-talent");
    }

    [Theory]
    [InlineData("acolyte-mercy", "acolyte-judgment")]
    [InlineData("acolyte-judgment", "acolyte-mercy")]
    public async Task LevelSixRadiantFlareNeedsNeitherPurifyNorSilenceAndKeepsBranchesExclusive(
        string chosenBranch, string excludedBranch)
    {
        await using var test = await FormalTreeContext.CreateAsync("acolyte", level: 6, points: 5);
        await PurchasePathAsync(test, "acolyte-prayer", "acolyte-doctrine", "acolyte-echo", chosenBranch);
        var (blocked, blockedError) = await test.Service.UnlockTalentNodeAsync("token", 1, excludedBranch);
        Assert.Null(blocked);
        Assert.Equal("SkillTalentBranchLocked", blockedError);

        var response = await PurchaseAvailableNodeAsync(test, "acolyte-radiant-flare-talent");

        Assert.Equal(0, response.TalentPoints);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "acolyte-radiant-flare");
        Assert.DoesNotContain(response.LearnedSkills, skill => skill.Code is "acolyte-purify" or "acolyte-silence");
    }

    [Theory]
    [InlineData("mage", "mage-barrage-talent", "mage-arcane-training")]
    [InlineData("mage", "mage-suppression-talent", "mage-spellbreak-talent")]
    [InlineData("mage", "mage-ice-armor", "mage-ward-training")]
    [InlineData("hunter", "hunter-mark-talent", "hunter-bow-training")]
    [InlineData("hunter", "hunter-volley-talent", "hunter-venom-talent")]
    [InlineData("hunter", "hunter-endurance", "hunter-mending-training")]
    [InlineData("rogue", "rogue-flurry-talent", "rogue-blade-training")]
    [InlineData("rogue", "rogue-gouge-talent", "rogue-poison-talent")]
    [InlineData("rogue", "rogue-hardiness", "rogue-recovery-training")]
    public async Task LevelFiveThirdTierNeedsFourPointsWithoutItsFormerMiddleNode(
        string profession, string targetCode, string formerMiddleCode)
    {
        await using var test = await FormalTreeContext.CreateAsync(profession, level: 5, points: 4);
        await PurchasePathAsync(test, RootCodes(profession));

        var response = await PurchaseAvailableNodeAsync(test, targetCode);

        Assert.Equal(0, response.TalentPoints);
        Assert.Equal(4, (await test.Db.CharacterSkillTalents.ToListAsync()).Sum(node => node.PointsSpent));
        Assert.DoesNotContain(await test.Db.CharacterSkillTalents.ToListAsync(), node => node.NodeCode == formerMiddleCode);
        Assert.False(response.TalentNodes.Single(node => node.Code == formerMiddleCode).CanUnlock);
        Assert.Equal("InsufficientTalentPoints",
            (await test.Service.UnlockTalentNodeAsync("token", 1, formerMiddleCode)).Error);
        Assert.Equal(4, (await test.Db.CharacterSkillTalents.ToListAsync()).Sum(node => node.PointsSpent));
    }

    [Theory]
    [InlineData("mage", "mage-suppression-talent", "mage-countermagic", "mage-barrage-talent", "mage-arcane-mastery")]
    [InlineData("hunter", "hunter-volley-talent", "hunter-toxin-training", "hunter-mark-talent", "hunter-predator")]
    [InlineData("rogue", "rogue-gouge-talent", "rogue-dirty-fighting", "rogue-flurry-talent", "rogue-relentless-assault")]
    public async Task AlternateRouteCannotReplaceACapstonesMandatoryCoreSkill(
        string profession, string routeCode, string rankTwoCode, string coreCode, string capstoneCode)
    {
        await using var test = await FormalTreeContext.CreateAsync(profession, level: 10, points: 9);
        await PurchasePathAsync(test, RootCodes(profession));
        await PurchasePathAsync(test, routeCode, rankTwoCode, rankTwoCode);

        var before = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.False(before.TalentNodes.Single(node => node.Code == capstoneCode).CanUnlock);
        Assert.Equal("SkillTalentPrerequisiteRequired",
            (await test.Service.UnlockTalentNodeAsync("token", 1, capstoneCode)).Error);
        Assert.Equal(3, test.Character.TalentPoints);

        await PurchaseAvailableNodeAsync(test, coreCode);
        var after = await PurchaseAvailableNodeAsync(test, capstoneCode);
        Assert.Equal(1, after.TalentPoints);
    }

    [Fact]
    public async Task AnyPrerequisiteStillRequiresTheChosenParentAtMaximumRank()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", level: 10, points: 9);
        await PurchasePathAsync(test, RootCodes("mage"));
        await PurchasePathAsync(test, "mage-barrage-talent", "mage-precision");

        Assert.False((await test.Service.GetAsync("token", 1)).Response!.TalentNodes
            .Single(node => node.Code == "mage-arcane-mastery").CanUnlock);
        Assert.Equal("SkillTalentPrerequisiteRequired",
            (await test.Service.UnlockTalentNodeAsync("token", 1, "mage-arcane-mastery")).Error);

        await PurchaseAvailableNodeAsync(test, "mage-precision");
        await PurchaseAvailableNodeAsync(test, "mage-arcane-mastery");
    }

    [Fact]
    public async Task MandatoryPrerequisiteStillRequiresMaximumRankEvenWhenAnAlternateIsComplete()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", level: 10, points: 9);
        await PurchasePathAsync(test, RootCodes("mage"));
        await PurchasePathAsync(test, "mage-suppression-talent", "mage-countermagic", "mage-countermagic", "mage-ice-armor");

        Assert.False((await test.Service.GetAsync("token", 1)).Response!.TalentNodes
            .Single(node => node.Code == "mage-frozen-heart").CanUnlock);
        Assert.Equal("SkillTalentPrerequisiteRequired",
            (await test.Service.UnlockTalentNodeAsync("token", 1, "mage-frozen-heart")).Error);

        await PurchaseAvailableNodeAsync(test, "mage-ice-armor");
        var response = await PurchaseAvailableNodeAsync(test, "mage-frozen-heart");
        Assert.Equal(0, response.TalentPoints);
    }

    [Fact]
    public async Task RelaxedRoutesDoNotBypassCharacterLevelOrAllowForeignProfessionNodes()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", level: 4, points: 3);
        await PurchasePathAsync(test, RootCodes("mage"));
        var before = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.False(before.TalentNodes.Single(node => node.Code == "mage-barrage-talent").CanUnlock);
        Assert.Equal("SkillTalentLevelRequired",
            (await test.Service.UnlockTalentNodeAsync("token", 1, "mage-barrage-talent")).Error);
        Assert.Equal("InvalidSkillTalent",
            (await test.Service.UnlockTalentNodeAsync("token", 1, "hunter-mark-talent")).Error);
        Assert.Equal(3, await test.Db.CharacterSkillTalents.CountAsync());
    }

    [Fact]
    public async Task ResetRefundsRelaxedHealingRouteAndKeepsPromotionSkills()
    {
        await using var test = await FormalTreeContext.CreateAsync("acolyte", level: 10, points: 9);
        await PurchasePathAsync(test, "acolyte-prayer", "acolyte-echo", "acolyte-heal-training", "acolyte-purify-talent");
        Assert.True(test.Character.TalentHealingDonePercent > 0);
        Assert.Null((await test.Service.PromoteAsync("token", 1,
            new PromoteCharacterRequest { ProfessionCode = "priest" })).Error);
        Assert.Null((await test.Service.SetSlotAsync("token", 1, 4,
            new SetSkillSlotRequest { SkillCode = "acolyte-purify", AutoUseEnabled = true, AutoHpThresholdPercent = 75 })).Error);

        var (response, error) = await test.Service.ResetTalentTreeAsync("token", 1);

        Assert.Null(error);
        Assert.Equal(9, response!.TalentPoints);
        Assert.Empty(await test.Db.CharacterSkillTalents.ToListAsync());
        Assert.Equal(0, test.Character.TalentHealingDonePercent);
        Assert.Equal(0, test.Character.TalentMaxHpPercent);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "priest-group-heal");
        Assert.Contains(response.Slots, slot => slot.SkillCode == "priest-group-heal");
        Assert.DoesNotContain(response.LearnedSkills, skill => skill.Code == "acolyte-purify");
        Assert.Null(response.Slots.Single(slot => slot.SlotIndex == 4).SkillCode);
        Assert.False(response.Slots.Single(slot => slot.SlotIndex == 4).AutoUseEnabled);
    }

    [Fact]
    public void PrerequisiteAccessibilityChangesPreserveEveryExistingNinePointBuildAndItsPassiveValues()
    {
        var snapshot = LoadPreAccessibilityTalents();
        var catalog = LoadProductionCatalog();
        Assert.Equal("3ecccc5d8772178abc2d63591d0da326993b5bbb", snapshot.SourceCommit);
        Assert.Equal(snapshot.TalentNodes.Select(node => node.Code).Order(),
            catalog.BaseProfessions.SelectMany(profession => catalog.TalentNodesForProfession(profession.Code))
                .Select(node => node.Code).Order());

        foreach (var previous in snapshot.TalentNodes)
        {
            var current = catalog.FindTalentNode(previous.Code)!;
            // No migration is required only while persisted ranks retain their cost,
            // rank limits, level gates, exclusive choices and cached passive values.
            Assert.Equal((previous.Cost, previous.MaxRank, previous.RequiredLevel, previous.RequiredTreePoints,
                    previous.ExclusiveGroup, previous.EffectCode, previous.ValuePerRank, previous.SkillCode),
                (current.Cost, current.MaxRank, current.RequiredLevel, current.RequiredTreePoints,
                    current.ExclusiveGroup, current.EffectCode, current.ValuePerRank, current.SkillCode));
            if (previous.Tier == snapshot.TalentNodes.Where(node => node.ProfessionCode == previous.ProfessionCode).Max(node => node.Tier))
                Assert.Equal(previous.Prerequisites.Order(), current.Prerequisites.Order());
        }

        foreach (var profession in catalog.BaseProfessions)
        {
            var previousNodes = snapshot.TalentNodes.Where(node => node.ProfessionCode == profession.Code)
                .OrderBy(node => node.Tier).ThenBy(node => node.Column).ToList();
            var previousBuilds = EnumerateLevelTenBuilds(previousNodes);
            Assert.NotEmpty(previousBuilds);
            foreach (var previousBuild in previousBuilds)
                Assert.True(CanPurchaseCompleteBuild(catalog, profession.Code, previousBuild),
                    $"Previously valid {profession.Code} build became inaccessible: {JsonSerializer.Serialize(previousBuild)}");
        }
    }

    private static bool CanPurchaseCompleteBuild(SkillCatalog catalog, string profession,
        IReadOnlyDictionary<string, int> requested)
    {
        var nodes = catalog.TalentNodesForProfession(profession);
        var purchased = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var budget = 9;
        while (budget > 0)
        {
            var available = nodes.FirstOrDefault(node =>
            {
                var rank = purchased.GetValueOrDefault(node.Code);
                return rank < requested.GetValueOrDefault(node.Code) && rank < node.MaxRank &&
                    10 >= node.RequiredLevel + rank && 9 - budget >= node.RequiredTreePoints &&
                    catalog.ArePrerequisitesMet(node, purchased) &&
                    (node.ExclusiveGroup is null || !nodes.Any(other => other.Code != node.Code &&
                        string.Equals(other.ExclusiveGroup, node.ExclusiveGroup, StringComparison.OrdinalIgnoreCase) &&
                        purchased.GetValueOrDefault(other.Code) > 0));
            });
            if (available is null) return false;
            purchased[available.Code] = purchased.GetValueOrDefault(available.Code) + 1;
            budget -= available.Cost;
        }
        return requested.All(node => purchased.GetValueOrDefault(node.Key) == node.Value);
    }

    private static PreAccessibilityTalentSnapshot LoadPreAccessibilityTalents() =>
        JsonSerializer.Deserialize<PreAccessibilityTalentSnapshot>(File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "talent-prerequisites-before-accessibility.json"))))!;

    private sealed class PreAccessibilityTalentSnapshot
    {
        public string SourceCommit { get; set; } = string.Empty;
        public List<SkillTalentNodeOptions> TalentNodes { get; set; } = [];
    }

    private static string[] RootCodes(string profession) => profession switch
    {
        "mage" => ["mage-arcane-insight", "mage-flow", "mage-frost-discipline"],
        "hunter" => ["hunter-keen-eye", "hunter-steady-hand", "hunter-fieldcraft"],
        "rogue" => ["rogue-killer-instinct", "rogue-light-fingers", "rogue-footwork"],
        _ => throw new ArgumentOutOfRangeException(nameof(profession))
    };

    private static async Task PurchasePathAsync(FormalTreeContext test, params string[] codes)
    {
        foreach (var code in codes) await PurchaseAvailableNodeAsync(test, code);
    }

    private static async Task<CharacterSkillsResponse> PurchaseAvailableNodeAsync(FormalTreeContext test, string code)
    {
        var (before, readError) = await test.Service.GetAsync("token", 1);
        Assert.Null(readError);
        Assert.True(before!.TalentNodes.Single(node => node.Code == code).CanUnlock, $"Response must permit purchasing {code}");
        var (after, purchaseError) = await test.Service.UnlockTalentNodeAsync("token", 1, code);
        Assert.Null(purchaseError);
        Assert.NotNull(after);
        return after;
    }
}
