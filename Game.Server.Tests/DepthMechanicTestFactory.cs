using Game.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace Game.Server.Tests;

internal static class DepthMechanicTestFactory
{
    public static MonsterCombatOptions WithCurrentDeclarations(MonsterCombatOptions options)
    {
        var configured = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build().GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        var progression = configured.DepthProgressions["legacy-depth"];
        var codes = progression.SelectMany(stage => stage.AddedSkills).Select(skill => skill.Code).ToHashSet();
        options.Skills.AddRange(configured.Skills.Where(skill => codes.Contains(skill.Code)));
        options.DepthProgressions["legacy-depth"] = progression;
        foreach (var profile in options.Profiles.Values) profile.DepthProgressionCode = "legacy-depth";
        return options;
    }
}
