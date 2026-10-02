using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public enum BattleActorKind { Character, Monster }
public sealed record BattleParticipant(RoomSlot Slot, Character Character);

public sealed class BattleActor
{
    public BattleParticipant? Participant { get; }
    public Monster? Monster { get; }
    public Character? Character => Participant?.Character;
    public CharacterCombatStatSnapshot? Stats { get; }
    public BattleActorKind Kind => Character is not null ? BattleActorKind.Character : BattleActorKind.Monster;
    public string ActorType => Kind.ToString();
    public int Id => Character?.Id ?? Monster!.Id;
    public int SlotIndex => Participant?.Slot.SlotIndex ?? 0;
    public string Name => Character?.Name ?? Monster!.Name;
    public string Label => Character is not null ? $"{SlotIndex}号位 {Name}" : Name;
    public int Hp { get => Character?.Hp ?? Monster!.Hp; set { if (Character is not null) Character.Hp = value; else Monster!.Hp = value; } }
    public int MaxHp => Character is { } character ? Stats!.WithCurrentMaxHp(character).MaxHp : Monster!.MaxHp;
    private BattleActor(BattleParticipant? participant, Monster? monster, CharacterCombatStatSnapshot? stats = null)
    {
        (Participant, Monster) = (participant, monster);
        Stats = participant is null ? null : stats ?? CharacterCombatStatSnapshot.Capture(participant.Character);
    }
    public static BattleActor ForCharacter(BattleParticipant participant) => new(participant, null);
    public static BattleActor ForCharacter(BattleParticipant participant, CharacterCombatStatSnapshot stats) => new(participant, null, stats);
    public static BattleActor ForMonster(Monster monster) => new(null, monster);
}

public sealed record BattleExecutionContext(Room Room, Monster Monster, IReadOnlyList<BattleParticipant> Party,
    IReadOnlyDictionary<int, ElementType> MainWeaponElements, IReadOnlyDictionary<int, OperationPotionBonuses> OperationBonuses,
    List<string> Logs, Func<Task<bool>>? InterruptIntent = null)
{
    private readonly IReadOnlyDictionary<int, CharacterCombatStatSnapshot> _characterStats = Party
        .DistinctBy(entry => entry.Character.Id).ToDictionary(entry => entry.Character.Id,
            entry => CharacterCombatStatSnapshot.Capture(entry.Character));
    public CharacterCombatStatSnapshot StatsFor(Character character) => _characterStats.TryGetValue(character.Id, out var stats)
        ? stats.WithCurrentMaxHp(character) : CharacterCombatStatSnapshot.Capture(character);
    public BattleActor Enemy => BattleActor.ForMonster(Monster);
    public IReadOnlyList<BattleActor> Characters => Party.OrderBy(entry => entry.Slot.SlotIndex)
        .Select(entry => BattleActor.ForCharacter(entry, StatsFor(entry.Character))).ToList();
}

public sealed record BattleDamageResult(int CalculatedAmount, int ActualAmount, bool IsCritical);
public sealed record BattleEffectOutcome(BattleEffectKind Kind, BattleActor Target, bool Applied,
    int CalculatedAmount = 0, int ActualAmount = 0, bool IsCritical = false,
    RemovedBattleStatus? RemovedStatus = null, string? StatusCode = null);

public sealed class BattleSkillResult
{
    public List<BattleEffectOutcome> Outcomes { get; } = [];
    public bool UseActualDamage { get; set; }
    public bool Applied => Outcomes.Any(outcome => outcome.Applied);
    public int Damage => SumDamage(UseActualDamage);
    public int ActualDamage => SumDamage(true);
    public bool HealingOccurred => Outcomes.Any(outcome => outcome.Kind == BattleEffectKind.Heal && outcome.ActualAmount > 0);
    public bool CleansingOccurred => Outcomes.Any(outcome => outcome.Kind == BattleEffectKind.Cleanse && outcome.RemovedStatus is not null);
    private int SumDamage(bool actual) => (int)Math.Min(int.MaxValue, Outcomes.Where(outcome => outcome.Kind == BattleEffectKind.Damage)
        .Sum(outcome => (long)(actual ? outcome.ActualAmount : outcome.CalculatedAmount)));
}

public sealed class BattleCastExecution
{
    public BattleExecutionContext Battle { get; }
    public BattleSkillDefinition Skill { get; }
    public CharacterSkillDefinition? CharacterSkill => Skill as CharacterSkillDefinition;
    public BattleActor Source { get; }
    public BattleStatusSource StatusSource => new(Source.ActorType, Source.Id, Skill.Code);
    public int? ChosenTargetId { get; init; }
    public int? IntentTargetId { get; init; }
    public IReadOnlyDictionary<string, int> ProfessionLevels { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<BattleSkillCooldown> Cooldowns { get; init; } = [];
    public BattleSkillResult Result { get; } = new();
    public decimal HealthPercentAtCast { get; }
    public decimal ConditionalDamageBonus { get; set; }
    public decimal AttackPowerBonus { get; set; }
    public List<decimal> DamageMultipliers { get; } = [];
    public decimal HealingMultiplier { get; set; } = 1;
    public bool GuardCounterEligible { get; set; }
    public bool DeduplicateHealing { get; set; }
    public decimal MonsterSkillReduction { get; set; }
    public decimal MonsterSkillBonusPercent { get; set; }
    public int LegacyGuardPower { get; set; }
    public bool IsBasicAttack { get; init; }
    public Dictionary<int, int> LegacyIncomingReduction { get; } = [];
    private readonly Dictionary<BattleEffectTarget, int[]> _fixedTargets = new();
    private readonly HashSet<int> _healedTargets = [];

    public BattleCastExecution(BattleExecutionContext battle, BattleSkillDefinition skill, BattleActor source)
    {
        (Battle, Skill, Source) = (battle, skill, source);
        var stats = source.Character is { } character ? battle.StatsFor(character) : null;
        HealthPercentAtCast = stats is not null ? WeaponCombatRules.HealthDamagePercent(source.Hp,
            stats.MaxHp, stats.StaminaPercent, stats.EnmityPercent) : 0;
        foreach (var effect in skill.Effects.Where(effect => effect.TargetPolicy.FixedAtCast))
            _fixedTargets.TryAdd(effect.TargetPolicy, SelectTargets(effect, ignoreFixed: true).Select(actor => actor.Id).ToArray());
    }

    public IReadOnlyList<BattleActor> SelectTargets(BattleSkillEffect effect, bool ignoreFixed = false)
    {
        if (effect.TargetPolicy.Side == BattleTargetSide.Self) return Source.Hp > 0 ? [Source] : [];
        if (Source.Kind == BattleActorKind.Character && effect.TargetPolicy.Side == BattleTargetSide.Opponent)
            return Battle.Monster.Hp > 0 ? [Battle.Enemy] : [];
        if (Source.Kind == BattleActorKind.Monster)
        {
            var alive = Battle.Characters.Where(actor => actor.Hp > 0).ToList();
            return effect.TargetPolicy.Selection == BattleTargetSelection.AllAlive ? alive :
                alive.Where(actor => actor.Id == IntentTargetId).ToList();
        }
        var characters = Battle.Characters;
        if (!ignoreFixed && _fixedTargets.TryGetValue(effect.TargetPolicy, out var fixedIds))
            return characters.Where(actor => actor.Hp > 0 && fixedIds.Contains(actor.Id)).ToList();
        var snapshot = characters.Select(actor => new SkillBattleActor(actor.Id, actor.SlotIndex, actor.Hp, actor.MaxHp, false));
        return SkillBattlePolicy.SelectAllies(effect, snapshot, Source.Id, ChosenTargetId)
            .Select(actor => characters.Single(character => character.Id == actor.Id)).ToList();
    }

    public bool CanHeal(BattleActor actor) => !DeduplicateHealing || _healedTargets.Add(actor.Id);
}
