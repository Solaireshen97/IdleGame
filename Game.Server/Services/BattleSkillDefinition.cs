using System.Collections.Immutable;
using Game.Server.Configuration;

namespace Game.Server.Services;

public enum BattleEffectKind { Damage, Heal, Guard, Cleanse, Dispel, Interrupt, ApplyStatus, CooldownReduction }
public enum BattleTargetSide { Self, Ally, Opponent }
public enum BattleTargetSelection { Primary, Self, Front, LowestHp, AllAlive, AllOtherAlive, FirstDebuffed }

public readonly record struct BattleEffectTarget(BattleTargetSide Side, BattleTargetSelection Selection,
    bool AllowsSelection, bool FixedAtCast, string Code)
{
    internal static BattleEffectTarget ForCharacter(string code) => code switch
    {
        "Monster" => new(BattleTargetSide.Opponent, BattleTargetSelection.Primary, false, false, code),
        "Self" => new(BattleTargetSide.Self, BattleTargetSelection.Self, false, false, code),
        "FrontAlly" => new(BattleTargetSide.Ally, BattleTargetSelection.Front, true, false, code),
        "FrontAllyFixed" => new(BattleTargetSide.Ally, BattleTargetSelection.Front, false, true, code),
        "LowestHpAlly" => new(BattleTargetSide.Ally, BattleTargetSelection.LowestHp, true, false, code),
        "LowestHpAllyFixed" => new(BattleTargetSide.Ally, BattleTargetSelection.LowestHp, false, true, code),
        "FirstDebuffedAlly" => new(BattleTargetSide.Ally, BattleTargetSelection.FirstDebuffed, true, false, code),
        "AllAlive" => new(BattleTargetSide.Ally, BattleTargetSelection.AllAlive, false, false, code),
        "AllOtherAlive" => new(BattleTargetSide.Ally, BattleTargetSelection.AllOtherAlive, false, false, code),
        _ => throw new InvalidOperationException($"Invalid character effect target: {code}")
    };

    internal static BattleEffectTarget ForMonster(string code) => code switch
    {
        "Self" => new(BattleTargetSide.Self, BattleTargetSelection.Self, false, false, code),
        "Front" => new(BattleTargetSide.Opponent, BattleTargetSelection.Front, false, true, code),
        "RandomAlive" => new(BattleTargetSide.Opponent, BattleTargetSelection.Primary, false, true, code),
        "AllAlive" => new(BattleTargetSide.Opponent, BattleTargetSelection.AllAlive, false, false, code),
        _ => throw new InvalidOperationException($"Invalid monster effect target: {code}")
    };
}

public sealed record BattleSkillEffect(BattleEffectKind Kind, BattleEffectTarget TargetPolicy, int Power = 0,
    decimal AttackPowerPercent = 100, decimal HealMaxHpPercent = 0, string? StatusCode = null,
    int DurationRounds = 0)
{
    // Stable wire names are projected from the typed runtime definition.
    public string Type => Kind.ToString();
    public string Target => TargetPolicy.Code;

    internal static BattleSkillEffect FromCharacter(CombatSkillEffectOptions effect) => new(
        Enum.Parse<BattleEffectKind>(effect.Type), BattleEffectTarget.ForCharacter(effect.Target),
        effect.Power, effect.AttackPowerPercent, effect.HealMaxHpPercent, effect.StatusCode, effect.DurationRounds);

    public static BattleSkillEffect Damage(decimal attackPowerPercent, int power = 0) => new(
        BattleEffectKind.Damage, BattleEffectTarget.ForCharacter("Monster"), power, attackPowerPercent);
}

public abstract record BattleSkillDefinition
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required int CooldownRounds { get; init; }
    public required int InitialCooldownRounds { get; init; }
    public required ImmutableArray<BattleSkillEffect> Effects { get; init; }
}

public sealed record CharacterSkillDefinition : BattleSkillDefinition
{
    public required string ProfessionCode { get; init; }
    public required int Level { get; init; }
    public required bool IsShared { get; init; }
    public required int UnlockLevel { get; init; }
    public required int Level2UnlockLevel { get; init; }
    public required int Level3UnlockLevel { get; init; }
    public required string AutoCondition { get; init; }
    public decimal ConditionalDamageBonusPercent { get; init; }
    public string? RequiredTargetStatusCode { get; init; }
    public int? TargetHpBelowPercent { get; init; }
    public int Power => Effects[0].Power;

    internal static CharacterSkillDefinition Compile(CombatSkillOptions skill, int level, bool isShared) => new()
    {
        Code = skill.Code, Name = skill.Name, Description = skill.Description, ProfessionCode = skill.ProfessionCode,
        CooldownRounds = skill.CooldownRounds, InitialCooldownRounds = skill.InitialCooldownRounds,
        Effects = SkillCatalog.EffectsFor(skill).Select(BattleSkillEffect.FromCharacter).ToImmutableArray(),
        Level = level, IsShared = isShared, UnlockLevel = skill.UnlockLevel,
        Level2UnlockLevel = skill.Level2UnlockLevel, Level3UnlockLevel = skill.Level3UnlockLevel,
        AutoCondition = SkillCatalog.AutoConditionFor(skill), ConditionalDamageBonusPercent = skill.ConditionalDamageBonusPercent,
        RequiredTargetStatusCode = skill.RequiredTargetStatusCode, TargetHpBelowPercent = skill.TargetHpBelowPercent
    };
}

public sealed record MonsterSkillDefinition : BattleSkillDefinition
{
    public required string TargetType { get; init; }
    public int? SelfHpBelowPercent { get; init; }
    public int? RoomRoundAtLeast { get; init; }
    public int ForcedPriority { get; init; }
    public bool IsInterruptible { get; init; }
    public required string DangerLevel { get; init; }

    internal static MonsterSkillDefinition Compile(MonsterSkillOptions skill)
    {
        var target = BattleEffectTarget.ForMonster(skill.TargetType);
        var effects = ImmutableArray.CreateBuilder<BattleSkillEffect>();
        if (skill.Effects is not null)
            effects.AddRange(skill.Effects.Select(effect => new BattleSkillEffect(Enum.Parse<BattleEffectKind>(effect.Type),
                BattleEffectTarget.ForMonster(effect.Target), effect.Power, effect.AttackPowerPercent,
                effect.HealMaxHpPercent, effect.StatusCode, effect.DurationRounds)));
        else
        {
            if (skill.DamagePowerPercent > 0)
                effects.Add(new(BattleEffectKind.Damage, target, AttackPowerPercent: skill.DamagePowerPercent));
            effects.AddRange(skill.Statuses.Select(status => new BattleSkillEffect(BattleEffectKind.ApplyStatus, target,
                AttackPowerPercent: 0, StatusCode: status.StatusCode, DurationRounds: status.DurationRounds)));
        }
        return new()
        {
            Code = skill.Code, Name = skill.Name, Description = skill.Description, CooldownRounds = skill.CooldownRounds,
            InitialCooldownRounds = skill.InitialCooldownRounds, Effects = effects.ToImmutable(), TargetType = skill.TargetType,
            SelfHpBelowPercent = skill.SelfHpBelowPercent, RoomRoundAtLeast = skill.RoomRoundAtLeast,
            ForcedPriority = skill.ForcedPriority, IsInterruptible = skill.IsInterruptible, DangerLevel = skill.DangerLevel
        };
    }
}

public sealed record MonsterProfileSkill(string Code, int Weight);
public sealed record MonsterCombatProfile(int SkillUseChancePercent, ImmutableArray<MonsterProfileSkill> Skills,
    bool UseEncounterLocalSkillClock = false);
