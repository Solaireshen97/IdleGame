using System.Text.Json;
using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class StoryQuestCatalog
{
    private static readonly HashSet<string> ObjectiveTypes = ["Dialogue", "DungeonClear", "EquipLootWeapon", "WeaponSkillLevel", "Plant", "ProduceAndEquipPotion", "FormationSaved"];
    private static readonly HashSet<string> RewardCodes = ["weapon-fragment-t1", "seed-peacebloom", "peacebloom"];
    private readonly Dictionary<string, StoryQuestDefinition> quests;
    private readonly Dictionary<string, StoryMapNodeDefinition> nodes;
    public static StoryQuestCatalog Default { get; } = LoadDefault();
    public IReadOnlyList<StoryQuestDefinition> Quests { get; }
    public IReadOnlyList<StoryMapNodeDefinition> MapNodes { get; }
    public IReadOnlyList<StoryNpcDefinition> Npcs { get; }
    public IReadOnlyList<StoryChapterDefinition> Chapters { get; }
    public StoryQuestDefinition FirstQuest => quests[Chapters[0].FirstQuestCode];

    public StoryQuestCatalog(IOptions<StoryOptions> options) : this(options.Value) { }

    public StoryQuestCatalog(StoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Quests = options.Quests.OrderBy(x => x.Order).ToArray();
        MapNodes = options.MapNodes.ToArray();
        Npcs = options.Npcs.ToArray();
        Chapters = options.Chapters.ToArray();
        quests = Unique(Quests, x => x.Code, "quest");
        nodes = Unique(MapNodes, x => x.Code, "map node");
        var npcs = Unique(Npcs, x => x.Code, "NPC");
        var chapters = Unique(Chapters, x => x.Code, "chapter");
        Require(Chapters.Count > 0 && Quests.Count > 0, "Story must contain a chapter and quests.");
        foreach (var q in Quests)
        {
            Require(chapters.ContainsKey(q.ChapterCode), $"Unknown chapter for {q.Code}.");
            Require(npcs.ContainsKey(q.StartNpcCode) && npcs.ContainsKey(q.TurnInNpcCode), $"Unknown NPC for {q.Code}.");
            Require(!string.IsNullOrWhiteSpace(q.StartDialogue) && !string.IsNullOrWhiteSpace(q.TurnInDialogue), $"Missing dialogue for {q.Code}.");
            Require(q.Revision > 0 && q.RequiredCount > 0 && q.RewardGold >= 0, $"Invalid quantities for {q.Code}.");
            Require(ObjectiveTypes.Contains(q.ObjectiveType), $"Unsupported objective for {q.Code}.");
            Require(q.ActorPolicy is "TutorialCharacter" or "AnyOwnedCharacter", $"Unsupported actor policy for {q.Code}.");
            Require(q.Rewards.All(r => RewardCodes.Contains(r.Code) && r.Quantity > 0), $"Invalid reward for {q.Code}.");
            Require(q.Rewards.Select(r => r.Code).Distinct(StringComparer.Ordinal).Count() == q.Rewards.Count, $"Duplicate reward for {q.Code}.");
            Require(q.NextQuestCode is null || quests.ContainsKey(q.NextQuestCode), $"Unknown successor for {q.Code}.");
            Require(q.UnlockMapNodeCodes.All(nodes.ContainsKey), $"Unknown map unlock for {q.Code}.");
            Require(q.ObjectiveType != "DungeonClear" || nodes.Values.Any(n => n.DungeonCode == q.TargetCode), $"Unknown dungeon target for {q.Code}.");
            Require(q.ObjectiveType != "Plant" || q.TargetCode == "peacebloom", $"Unknown planting target for {q.Code}.");
            Require(q.ObjectiveType != "ProduceAndEquipPotion" || q.TargetCode == "minor-healing-potion", $"Unknown potion target for {q.Code}.");
        }
        foreach (var n in MapNodes)
        {
            Require(n.Code == n.DungeonCode && !string.IsNullOrWhiteSpace(n.RegionCode), $"Invalid dungeon reference for {n.Code}.");
            Require(quests.TryGetValue(n.RequiredQuestCode, out var q) && q.UnlockMapNodeCodes.Contains(n.Code), $"Missing map prerequisite for {n.Code}.");
        }
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chapter in Chapters)
        {
            Require(quests.ContainsKey(chapter.FirstQuestCode) && quests.ContainsKey(chapter.LastQuestCode), $"Unknown chapter endpoints for {chapter.Code}.");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = quests[chapter.FirstQuestCode];
            while (true)
            {
                Require(visited.Add(current.Code), $"Quest cycle at {current.Code}.");
                Require(current.ChapterCode == chapter.Code, $"Quest leaves chapter {chapter.Code}.");
                reachable.Add(current.Code);
                if (current.NextQuestCode is null)
                {
                    Require(current.Code == chapter.LastQuestCode, $"Wrong chapter terminal for {chapter.Code}.");
                    break;
                }
                current = quests[current.NextQuestCode];
            }
        }
        Require(reachable.Count == quests.Count, "Unreachable story quests.");
    }

    public StoryQuestDefinition? FindQuest(string code) => quests.GetValueOrDefault(code);
    public StoryMapNodeDefinition? FindMapNode(string code) => nodes.GetValueOrDefault(code);

    public void ValidateAgainstWorld(WorldCatalog world, MaterialCatalog materials)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(materials);
        foreach (var node in MapNodes)
        {
            var dungeon = world.Dungeons.SingleOrDefault(d => d.Code == node.DungeonCode);
            Require(dungeon is not null && dungeon.RegionCode == node.RegionCode && dungeon.IsVisible,
                $"Story map node {node.Code} must reference a visible dungeon in region {node.RegionCode}.");
        }
        foreach (var quest in Quests)
        {
            Require(quest.ObjectiveType != "DungeonClear" || world.Dungeons.Any(d => d.Code == quest.TargetCode && d.IsVisible),
                $"Unknown world dungeon target for {quest.Code}.");
            foreach (var reward in quest.Rewards)
                Require(materials.FindItem(reward.Code) is not null, $"Unknown material reward {reward.Code} for {quest.Code}.");
        }
    }

    private static Dictionary<string, T> Unique<T>(IEnumerable<T> values, Func<T, string> code, string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
            Require(!string.IsNullOrWhiteSpace(code(value)) && result.TryAdd(code(value), value), $"Empty or duplicate {kind} code: {code(value)}.");
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static StoryQuestCatalog LoadDefault()
    {
        using var stream = typeof(StoryQuestCatalog).Assembly.GetManifestResourceStream("Game.Server.story.json")
            ?? throw new InvalidOperationException("Embedded story.json is missing.");
        using var document = JsonDocument.Parse(stream);
        var options = document.RootElement.GetProperty(StoryOptions.SectionName).Deserialize<StoryOptions>()
            ?? throw new InvalidOperationException("Story configuration is empty.");
        return new StoryQuestCatalog(options);
    }
}
