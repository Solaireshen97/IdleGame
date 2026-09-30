using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class SkillCooldownPolicyTests
{
    [Fact]
    public void CooldownReductionNeedsAnotherDamageSkillAndIgnoresItsOwnCooldown()
    {
        var catalog = new SkillCatalog(Options.Create(new SkillOptions
        {
            Professions = [new() { Code = "test", Name = "Test", StartingSkills = ["accelerate"] }],
            Abilities = [new() { Code = "accelerate", ProfessionCode = "test", Name = "Accelerate", Description = "Reduce damage cooldowns",
                Effects = [new() { Type = "CooldownReduction", Target = "Self", Power = 1 }] }]
        }));
        var skill = catalog.Resolve(new Character { ProfessionCode = "test", Level = 1 }, "accelerate")!;
        var state = new SkillBattleSnapshot(1, [new(1, 1, 100, 100, false)], 100, 100, false, false, false, false, []);
        Assert.Equal("NoReducibleCooldown", SkillBattlePolicy.Availability(skill, state).UnavailableReason);
        Assert.False(SkillBattlePolicy.HasApplicableEffect(skill, state with { DamageSkillsOnCooldown = ["accelerate"] }));
        Assert.True(SkillBattlePolicy.HasApplicableEffect(skill, state with { DamageSkillsOnCooldown = ["strike"] }));
    }
}
