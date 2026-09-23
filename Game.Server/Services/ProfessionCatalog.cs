using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Dtos.Professions;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ProfessionCatalog
{
    public const string GatheringCode = "Gathering";
    public const string AlchemyCode = "Alchemy";
    public static ProfessionCatalog Default { get; } = new(Options.Create(new ProfessionProgressionOptions
    {
        ExperienceToNextLevel = [30, 60, 100, 150, 220, 300, 390, 490, 600]
    }));

    private readonly ProfessionProgressionOptions options;
    private readonly Dictionary<string, ProfessionTalentNodeOptions> nodes;

    public ProfessionCatalog(IOptions<ProfessionProgressionOptions> configured)
    {
        options = configured.Value;
        if (options.MaximumLevel is < 2 or > 50 || options.ExperienceToNextLevel.Count != options.MaximumLevel - 1 ||
            options.ExperienceToNextLevel.Any(value => value <= 0) ||
            options.GatheringExperiencePerCycle <= 0 || options.RareGatheringExperiencePerCycle <= 0 ||
            options.AlchemyExperiencePerCycle <= 0)
            throw new InvalidOperationException("Invalid profession progression configuration.");
        nodes = new Dictionary<string, ProfessionTalentNodeOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in options.TalentNodes)
        {
            if (string.IsNullOrWhiteSpace(node.Code) || string.IsNullOrWhiteSpace(node.Name) ||
                string.IsNullOrWhiteSpace(node.Description) || !IsValidProfession(node.ProfessionCode) ||
                node.EffectType is not ("ExtraYieldChancePercent" or "RareBonusChancePercent" or
                    "CycleReductionSeconds" or "IngredientSaveChancePercent") ||
                node.ProfessionCode == GatheringCode && node.EffectType == "IngredientSaveChancePercent" ||
                node.ProfessionCode == AlchemyCode && node.EffectType == "RareBonusChancePercent" ||
                node.ValuePerRank is < 1 or > 100 || node.MaxRank is < 1 or > 5 ||
                node.MinimumLevel < 2 || node.MinimumLevel > options.MaximumLevel ||
                node.Tier is < 1 or > 10 ||
                node.EffectType.EndsWith("ChancePercent", StringComparison.Ordinal) &&
                    node.ValuePerRank * node.MaxRank > 100 || !nodes.TryAdd(node.Code, node))
                throw new InvalidOperationException($"Invalid profession talent: {node.Code}");
        }
        foreach (var node in nodes.Values)
        {
            if (node.PrerequisiteCode is null) continue;
            if (!nodes.TryGetValue(node.PrerequisiteCode, out var required) ||
                required.ProfessionCode != node.ProfessionCode || required.Tier >= node.Tier ||
                node.PrerequisiteRank < 1 || node.PrerequisiteRank > required.MaxRank)
                throw new InvalidOperationException($"Invalid profession talent prerequisite: {node.Code}");
        }
    }

    public static bool IsValidProfession(string code) => code is GatheringCode or AlchemyCode;
    public ProfessionTalentNodeOptions? FindNode(string code) => nodes.GetValueOrDefault(code);
    public int GatheringExperiencePerCycle => options.GatheringExperiencePerCycle;
    public int RareGatheringExperiencePerCycle => options.RareGatheringExperiencePerCycle;
    public int AlchemyExperiencePerCycle => options.AlchemyExperiencePerCycle;

    public int? ExperienceToNextLevel(int level) => level >= options.MaximumLevel ? null :
        options.ExperienceToNextLevel[Math.Clamp(level, 1, options.MaximumLevel - 1) - 1];

    public void GrantExperience(Character character, string professionCode, long amount)
    {
        if (amount <= 0 || !IsValidProfession(professionCode)) return;
        var level = professionCode == GatheringCode ? character.GatheringLevel : character.AlchemyLevel;
        var experience = professionCode == GatheringCode ? character.GatheringExperience : character.AlchemyExperience;
        var points = professionCode == GatheringCode ? character.GatheringTalentPoints : character.AlchemyTalentPoints;
        if (level >= options.MaximumLevel) return;
        var total = (long)experience + amount;
        while (level < options.MaximumLevel && total >= options.ExperienceToNextLevel[level - 1])
        {
            total -= options.ExperienceToNextLevel[level - 1];
            level++;
            points++;
        }
        if (level == options.MaximumLevel) total = 0;
        if (professionCode == GatheringCode)
        {
            character.GatheringLevel = level;
            character.GatheringExperience = checked((int)total);
            character.GatheringTalentPoints = points;
        }
        else
        {
            character.AlchemyLevel = level;
            character.AlchemyExperience = checked((int)total);
            character.AlchemyTalentPoints = points;
        }
        character.Version++;
    }

    public int EffectValue(IEnumerable<CharacterProfessionTalent> talents, string professionCode, string effectType) =>
        talents.Where(talent => talent.ProfessionCode == professionCode &&
            nodes.TryGetValue(talent.NodeCode, out var node) && node.EffectType == effectType)
            .Sum(talent => talent.Rank * nodes[talent.NodeCode].ValuePerRank);

    public async Task<ProfessionProgressResponse> BuildProgressAsync(GameDbContext db, Character character, string code)
    {
        var activityKind = code == GatheringCode ? CharacterActivityManager.GatheringKind : CharacterActivityManager.ProductionKind;
        var locked = await db.CharacterActivities.AsNoTracking().AnyAsync(activity =>
            activity.CharacterId == character.Id && activity.Kind == activityKind);
        var talents = await db.CharacterProfessionTalents.AsNoTracking()
            .Where(talent => talent.CharacterId == character.Id && talent.ProfessionCode == code)
            .ToDictionaryAsync(talent => talent.NodeCode, talent => talent.Rank);
        var level = code == GatheringCode ? character.GatheringLevel : character.AlchemyLevel;
        var available = code == GatheringCode ? character.GatheringTalentPoints : character.AlchemyTalentPoints;
        return new ProfessionProgressResponse
        {
            ProfessionCode = code,
            ProfessionName = code == GatheringCode ? "草药采集" : "炼金生产",
            Level = level,
            Experience = code == GatheringCode ? character.GatheringExperience : character.AlchemyExperience,
            ExperienceToNextLevel = ExperienceToNextLevel(level),
            AvailableTalentPoints = available,
            IsTalentLocked = locked,
            Nodes = nodes.Values.Where(node => node.ProfessionCode == code)
                .OrderBy(node => node.Tier).ThenBy(node => node.Code).Select(node =>
                {
                    var rank = talents.GetValueOrDefault(node.Code);
                    var prerequisite = node.PrerequisiteCode is null ? null : nodes[node.PrerequisiteCode];
                    var reason = locked ? code == GatheringCode ? "采集中不能修改采集天赋" : "炼金中不能修改炼金天赋"
                        : rank >= node.MaxRank ? "已达到上限" : level < node.MinimumLevel
                        ? $"需要专业 Lv.{node.MinimumLevel}" : prerequisite is not null &&
                        talents.GetValueOrDefault(prerequisite.Code) < node.PrerequisiteRank
                            ? $"需要「{prerequisite.Name}」{node.PrerequisiteRank} 级" : available == 0 ? "天赋点不足" : null;
                    return new ProfessionTalentNodeResponse
                    {
                        Code = node.Code, Name = node.Name, Description = node.Description,
                        Tier = node.Tier, Rank = rank, MaxRank = node.MaxRank,
                        MinimumLevel = node.MinimumLevel, PrerequisiteName = prerequisite?.Name,
                        PrerequisiteRank = prerequisite is null ? 0 : node.PrerequisiteRank,
                        CanPurchase = reason is null, LockReason = reason
                    };
                }).ToList()
        };
    }
}

public static class ProfessionRoll
{
    public static bool Succeeds(int taskId, int cycle, int salt, int chancePercent)
    {
        if (chancePercent <= 0) return false;
        if (chancePercent >= 100) return true;
        ulong value = (uint)taskId;
        value = value * 0x9E3779B97F4A7C15UL + (uint)cycle;
        value = value * 0xBF58476D1CE4E5B9UL + (uint)salt;
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return value % 100 < (ulong)chancePercent;
    }
}
