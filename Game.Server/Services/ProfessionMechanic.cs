namespace Game.Server.Services;

public class ProfessionCastMechanic
{
    public virtual Task PrepareAsync(BattleCastExecution cast, BattleEffectExecutor executor) => Task.CompletedTask;
    public virtual BattleSkillEffect TransformEffect(BattleCastExecution cast, BattleSkillEffect effect, BattleEffectExecutor executor) => effect;
    public virtual IReadOnlyList<BattleActor> OrderTargets(BattleCastExecution cast, BattleSkillEffect effect,
        IReadOnlyList<BattleActor> targets) => targets;
    public virtual Task AfterDamageTargetAsync(BattleCastExecution cast, BattleEffectOutcome outcome,
        BattleEffectExecutor executor) => Task.CompletedTask;
    public virtual Task AfterEffectAsync(BattleCastExecution cast, BattleSkillEffect effect, IReadOnlyList<BattleEffectOutcome> outcomes,
        BattleEffectExecutor executor) => Task.CompletedTask;
    public virtual Task AfterCastAsync(BattleCastExecution cast, BattleEffectExecutor executor) => Task.CompletedTask;
}

public sealed class ProfessionMechanicRegistry(ProfessionMechanicCatalog? mechanics = null)
{
    private readonly ProfessionMechanicCatalog _mechanics = mechanics ?? ProfessionMechanicCatalog.Default;
    public ProfessionCastMechanic For(CharacterSkillDefinition skill) => skill.ProfessionCode.ToLowerInvariant() switch
    {
        "swordsman" or "knight" => new KnightMechanics(_mechanics),
        "rogue" => new RogueMechanics(_mechanics),
        "acolyte" when !skill.IsShared => new AcolyteMechanics(_mechanics),
        "hunter" when !skill.IsShared => new HunterMechanics(_mechanics),
        "mage" when !skill.IsShared => new MageMechanics(_mechanics),
        _ => new ProfessionCastMechanic()
    };
}
