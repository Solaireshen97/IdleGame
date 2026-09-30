using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class CombatSkillLoadoutCodecTests
{
    [Fact]
    public void LegacyArrayAndVersionedSnapshotPreserveValidSharedAndAutomaticSettings()
    {
        var character = new Character { ProfessionCode = "cleric", Level = 10 };
        var levels = new Dictionary<string, int> { ["knight"] = 30 };
        var slots = new[]
        {
            new CharacterSkillSlot { SlotIndex = 1, SkillCode = "cleric-heal", AutoUseEnabled = true,
                AutoConditionOverride = "AllyHpBelowThreshold", AutoHpThresholdPercent = 42 },
            new CharacterSkillSlot { SlotIndex = 2, SkillCode = "knight-hit", AutoUseEnabled = true,
                AutoConditionOverride = "Always", AutoHpThresholdPercent = 75 }
        };
        var versioned = CombatSkillLoadoutCodec.Capture(slots);
        using var document = JsonDocument.Parse(versioned);
        Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        var expected = CombatSkillLoadoutCodec.Restore(versioned, character, Catalog(), levels);
        var legacy = JsonSerializer.Serialize(slots.Select(slot => new CombatSkillLoadoutSlot(
            slot.SlotIndex, slot.SkillCode, slot.AutoUseEnabled, slot.AutoConditionOverride, slot.AutoHpThresholdPercent)));
        Assert.Equal(expected.ToArray(), CombatSkillLoadoutCodec.Restore(legacy, character, Catalog(), levels).ToArray());
        Assert.Equal("knight-hit", expected[1].SkillCode);
        Assert.True(expected[0].AutoUseEnabled);
        Assert.Equal("AllyHpBelowThreshold", expected[0].AutoConditionOverride);
        Assert.Equal(42, expected[0].AutoHpThresholdPercent);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"SlotIndex\":0}]")]
    [InlineData("[{\"SlotIndex\":1},{\"SlotIndex\":1}]")]
    [InlineData("[null]")]
    public void DamagedOrStructurallyInvalidSnapshotRestoresSafeNativeDefaults(string json)
    {
        var restored = CombatSkillLoadoutCodec.Restore(json,
            new Character { ProfessionCode = "cleric", Level = 10 }, Catalog(), new Dictionary<string, int>());
        var slot = Assert.Single(restored);
        Assert.Equal("cleric-heal", slot.SkillCode);
        Assert.False(slot.AutoUseEnabled);
        Assert.Null(slot.AutoConditionOverride);
        Assert.Equal(SkillRules.DefaultAutoHpThresholdPercent, slot.AutoHpThresholdPercent);
    }

    [Fact]
    public void ExplicitlyEmptyLegacyLoadoutRemainsEmpty()
    {
        Assert.Empty(CombatSkillLoadoutCodec.Restore("[]",
            new Character { ProfessionCode = "cleric", Level = 10 }, Catalog(), new Dictionary<string, int>()));
    }

    [Fact]
    public void UnknownVersionIsRejectedInsteadOfReplaced()
    {
        Assert.Throws<UnsupportedCombatSkillLoadoutVersionException>(() => CombatSkillLoadoutCodec.Restore(
            "{\"SchemaVersion\":2,\"Slots\":[]}", new Character { ProfessionCode = "cleric", Level = 10 },
            Catalog(), new Dictionary<string, int>()));
    }

    [Fact]
    public void RestorationEnforcesOneSharedSkillUniqueSkillsAndSafeAutomaticOptions()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new CombatSkillLoadoutSlot(1, " cleric-heal ", true, "invalid", 101),
            new CombatSkillLoadoutSlot(2, "knight-hit", true, "Always", 45),
            new CombatSkillLoadoutSlot(3, "mage-fire", true, "Always", 50),
            new CombatSkillLoadoutSlot(4, "cleric-heal", true, "Always", 30),
            new CombatSkillLoadoutSlot(5, "missing", true, "Always", 30)
        });
        var restored = CombatSkillLoadoutCodec.Restore(json,
            new Character { ProfessionCode = "cleric", Level = 10 }, Catalog(),
            new Dictionary<string, int> { ["knight"] = 30, ["mage"] = 30 });
        Assert.Null(restored[0].AutoConditionOverride);
        Assert.Equal(SkillRules.DefaultAutoHpThresholdPercent, restored[0].AutoHpThresholdPercent);
        Assert.True(restored[0].AutoUseEnabled);
        Assert.Equal("knight-hit", restored[1].SkillCode);
        Assert.All(restored.Skip(2), slot =>
        {
            Assert.Null(slot.SkillCode);
            Assert.False(slot.AutoUseEnabled);
            Assert.Null(slot.AutoConditionOverride);
        });
    }

    [Theory]
    [InlineData(9, 30)]
    [InlineData(10, 29)]
    public void ArchivedSharedSkillCannotBypassEitherLevelRequirement(int activeLevel, int sourceLevel)
    {
        var json = JsonSerializer.Serialize(new[] { new CombatSkillLoadoutSlot(1, "knight-hit", true, "Always", 50) });
        var restored = CombatSkillLoadoutCodec.Restore(json,
            new Character { ProfessionCode = "cleric", Level = activeLevel }, Catalog(),
            new Dictionary<string, int> { ["knight"] = sourceLevel });
        Assert.Null(Assert.Single(restored).SkillCode);
    }

    internal static SkillCatalog Catalog() => new(Options.Create(new SkillOptions
    {
        Professions =
        [
            new ProfessionOptions { Code = "knight", Name = "骑士", StartingSkills = ["knight-hit"] },
            new ProfessionOptions { Code = "cleric", Name = "牧师", StartingSkills = ["cleric-heal"] },
            new ProfessionOptions { Code = "mage", Name = "法师", StartingSkills = ["mage-fire"] }
        ],
        Abilities =
        [
            new CombatSkillOptions { Code = "knight-hit", ProfessionCode = "knight", Name = "斩击",
                Description = "伤害", EffectType = "Damage", Power = 10, UnlockLevel = 1 },
            new CombatSkillOptions { Code = "cleric-heal", ProfessionCode = "cleric", Name = "治疗",
                Description = "治疗", EffectType = "Heal", Power = 10, UnlockLevel = 1 },
            new CombatSkillOptions { Code = "mage-fire", ProfessionCode = "mage", Name = "火球",
                Description = "伤害", EffectType = "Damage", Power = 10, UnlockLevel = 1 }
        ]
    }));
}
