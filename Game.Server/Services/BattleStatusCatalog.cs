using Game.Server.Configuration;
using Game.Shared.Enums;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

/// <summary>Definitions shared by player skills, monster skills and consumables.</summary>
public sealed class BattleStatusCatalog
{
    private readonly Dictionary<string, BattleStatusDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    public BattleStatusCatalog(IOptions<MonsterCombatOptions> options)
    {
        foreach (var status in options.Value.StatusEffects)
        {
            if (string.IsNullOrWhiteSpace(status.Code) || string.IsNullOrWhiteSpace(status.Name) ||
                string.IsNullOrWhiteSpace(status.Description) ||
                status.EffectType is not ("None" or "ActionBlocked" or "AttackPercent" or "ReductionPercent" or "DamageTakenPercent" or "DamageDealtPercent" or "DoubleAttackChancePercent" or "DamageOverTime" or "HealOverTime" or "SilenceNextIntent") ||
                status.ValuePerStack == 0 && status.EffectType is not ("None" or "DamageOverTime" or "HealOverTime") ||
                status.MaxStacks is < 1 or > 10 ||
                status.Stacking is not ("RefreshDuration" or "AddStack" or "ReplaceIfStronger") ||
                !Enum.IsDefined(status.Lifetime) || !Enum.IsDefined(status.CounterKind) ||
                !Enum.IsDefined(status.DamageScope) ||
                status.DamageScope != BattleDamageScope.All && status.EffectType != "DamageTakenPercent" ||
                !Enum.IsDefined(status.Mechanic) || !Enum.IsDefined(status.SnapshotRefresh) ||
                !Enum.IsDefined(status.FamilyRefresh) || status.InitialStacks < 1 || status.InitialStacks > status.MaxStacks ||
                status.MechanicLevel is < 0 or > 10 || status.MechanicPower is < 0 or > 1000 ||
                status.FamilyCode is not null && string.IsNullOrWhiteSpace(status.FamilyCode) ||
                !_definitions.TryAdd(status.Code, BattleStatusDefinition.Compile(status)))
                throw new InvalidOperationException($"Invalid battle status configuration: {status.Code}");
        }
        AddCoreStatus(BattleGuardService.GuardCode, "守护", "本回合受到的直接伤害降低。",
            BattleStatusMechanic.Guard, BattleStatusCounterKind.None);
        AddCoreStatus(BattleGuardService.PermissionCode, "守护反击资格", "受到直接攻击后，使实际保护者获得一次反击。",
            BattleStatusMechanic.GuardCounterPermission, BattleStatusCounterKind.Charges);
        AddCoreStatus(BattleGuardService.ReadyCode, "待反击", "当前怪物行动结算后发动一次守护反击。",
            BattleStatusMechanic.GuardCounterattack, BattleStatusCounterKind.Charges);
        AddCoreStatus("mage-echo-used", "回响已触发", "本回合已触发失序回响，剩余失序保留至后续回合。",
            BattleStatusMechanic.MageEchoUsed, BattleStatusCounterKind.None);
        AddHistoricalEcho("talent-sword-rhythm", "节奏追击", 25);
        AddHistoricalEcho("talent-intercept-echo", "截击追击", 50);
        AddHistoricalEcho("talent-guard-echo", "反攻追击", 75);
        _definitions.Add(BattleStatusService.ActionSkippedCode, BattleStatusDefinition.Compile(new()
        {
            Code = BattleStatusService.ActionSkippedCode, Name = "本回合行动已跳过", Description = "解除控制后不补行动。",
            EffectType = "None", IsPositive = true, IsDispellable = false, IsHidden = true,
            Lifetime = BattleStatusLifetime.CurrentRound, CounterKind = BattleStatusCounterKind.None
        }));
    }

    private void AddHistoricalEcho(string code, string name, decimal power)
    {
        if (_definitions.TryGetValue(code, out var configured))
        {
            if (configured.Mechanic != BattleStatusMechanic.NormalAttackEcho || configured.Lifetime != BattleStatusLifetime.Rounds ||
                configured.CounterKind != BattleStatusCounterKind.Charges || configured.MaxStacks != 1 || !configured.IsPositive || configured.IsDispellable)
                throw new InvalidOperationException($"Invalid historical battle status: {code}");
            return;
        }
        _definitions.Add(code, BattleStatusDefinition.Compile(new()
        {
            Code = code, Name = name, Description = $"下次普通攻击附加 {power:0.##}% 追击；消费后移除。",
            EffectType = "None", IsPositive = true, IsDispellable = false, Lifetime = BattleStatusLifetime.Rounds,
            CounterKind = BattleStatusCounterKind.Charges, Mechanic = BattleStatusMechanic.NormalAttackEcho, MechanicPower = power
        }));
    }

    private void AddCoreStatus(string code, string name, string description, BattleStatusMechanic mechanic,
        BattleStatusCounterKind counter)
    {
        if (_definitions.TryGetValue(code, out var configured))
        {
            if (configured.Mechanic != mechanic || configured.Lifetime != BattleStatusLifetime.CurrentRound ||
                configured.CounterKind != counter || !configured.IsPositive || configured.IsDispellable)
                throw new InvalidOperationException($"Invalid core battle status: {code}");
            return;
        }
        _definitions.Add(code, BattleStatusDefinition.Compile(new()
        {
            Code = code, Name = name, Description = description, EffectType = "None", IsPositive = true,
            IsDispellable = false, Lifetime = BattleStatusLifetime.CurrentRound, CounterKind = counter,
            Mechanic = mechanic, Stacking = "ReplaceIfStronger"
        }));
    }

    public IReadOnlyCollection<BattleStatusDefinition> Definitions => _definitions.Values;
    public BattleStatusDefinition? Find(string? code) => code is not null &&
        _definitions.TryGetValue(code, out var definition) ? definition : null;

    public BattleStatusDefinition? FindMechanic(BattleStatusMechanic mechanic, int? level = null, decimal? power = null) =>
        _definitions.Values.FirstOrDefault(status => status.Mechanic == mechanic &&
            (!level.HasValue || status.MechanicLevel == level) && (!power.HasValue || status.FamilyStrength == power));
}
