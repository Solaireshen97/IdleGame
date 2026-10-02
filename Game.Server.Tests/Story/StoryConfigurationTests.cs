using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests.Story;

public class StoryConfigurationTests
{
    private static StoryOptions ReadOptions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.File("Game.Server", "story.json")));
        return document.RootElement.GetProperty("Story").Deserialize<StoryOptions>()!;
    }

    [Fact]
    public void FirstChapterHasReachableTutorialAndRealMapRoutes()
    {
        var catalog = StoryQuestCatalog.Default;
        Assert.Equal(12, catalog.Quests.Count);
        Assert.Equal("ch01-01", catalog.FirstQuest.Code);
        Assert.Equal("ProduceAndEquipPotion", catalog.FindQuest("ch01-06")!.ObjectiveType);
        Assert.Equal("FormationSaved", catalog.FindQuest("ch01-10")!.ObjectiveType);
        Assert.Equal(2, catalog.FindQuest("ch01-04")!.RequiredCount);
        Assert.Equal("minor-healing-potion", catalog.FindQuest("ch01-06")!.TargetCode);
        using var world = JsonDocument.Parse(File.ReadAllText(TestRepository.File("Game.Server", "world.json")));
        var dungeons = world.RootElement.GetProperty("World").GetProperty("Dungeons").EnumerateArray()
            .Select(d => d.GetProperty("Code").GetString()).ToHashSet();
        Assert.All(catalog.MapNodes, node => Assert.Contains(node.DungeonCode, dungeons));
        Assert.Equal(7, catalog.MapNodes.Count);
        Assert.Equal(2, catalog.FindQuest("ch01-12")!.UnlockMapNodeCodes.Count);
        Assert.Equal(190, catalog.Quests.Sum(q => q.RewardGold));
    }

    [Fact]
    public void RejectsCyclesAndUnreachableQuests()
    {
        var options = ReadOptions();
        options.Quests[11].NextQuestCode = "ch01-01";
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
        options = ReadOptions();
        options.Quests[0].NextQuestCode = "ch01-03";
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
    }

    [Fact]
    public void RejectsUnknownRewardsTargetsAndNpcReferences()
    {
        var options = ReadOptions();
        options.Quests[1].Rewards[0].Code = "arbitrary-item";
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
        options = ReadOptions();
        options.Quests[1].TargetCode = "missing-dungeon";
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
        options = ReadOptions();
        options.Quests[0].StartNpcCode = "missing-npc";
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
    }

    [Fact]
    public void RejectsDuplicateCodesAndUnownedMapUnlocks()
    {
        var options = ReadOptions();
        options.Quests[1].Code = options.Quests[0].Code;
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
        options = ReadOptions();
        options.MapNodes[0].RequiredQuestCode = "ch01-02";
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonpositiveRewardQuantities(int quantity)
    {
        var options = ReadOptions();
        options.Quests[1].Rewards[0].Quantity = quantity;
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options));
    }

    [Fact]
    public void TutorialSuppliesArriveBeforeTheyAreNeeded()
    {
        var catalog = StoryQuestCatalog.Default;
        Assert.Contains(catalog.FindQuest("ch01-02")!.Rewards, r => r.Code == "weapon-fragment-t1" && r.Quantity == 2);
        Assert.Contains(catalog.FindQuest("ch01-04")!.Rewards, r => r.Code == "seed-peacebloom" && r.Quantity == 1);
        Assert.Contains(catalog.FindQuest("ch01-05")!.Rewards, r => r.Code == "peacebloom" && r.Quantity == 2);
        Assert.All(catalog.Quests.Take(6), q => Assert.Equal("TutorialCharacter", q.ActorPolicy));
        Assert.DoesNotContain(catalog.Quests.SelectMany(q => q.Rewards), r => r.Code.Contains("weapon") && r.Code != "weapon-fragment-t1");
    }

    [Fact]
    public void WorldValidationRejectsMissingDungeonsAndMaterials()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestRepository.File("Game.Server", "world.json"))
            .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var world = WorldCatalog.LoadDefault();
        var materials = new MaterialCatalog(Options.Create(configuration.GetSection("Materials").Get<MaterialOptions>()!));
        var catalog = new StoryQuestCatalog(ReadOptions());
        catalog.ValidateAgainstWorld(world, materials);
        Assert.Throws<InvalidOperationException>(() => catalog.ValidateAgainstWorld(world,
            new MaterialCatalog(Options.Create(new MaterialOptions()))));
        var options = ReadOptions();
        options.MapNodes[0].RegionCode = "missing-region";
        Assert.Throws<InvalidOperationException>(() => new StoryQuestCatalog(options).ValidateAgainstWorld(world, materials));
    }
}
