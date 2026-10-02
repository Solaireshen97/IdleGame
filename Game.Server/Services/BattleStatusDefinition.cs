using Game.Server.Configuration;
using Game.Shared.Enums;

namespace Game.Server.Services;

public sealed record BattleStatusDefinition(string Code, string Name, string Description, string EffectType,
    decimal ValuePerStack, int MaxStacks, string Stacking, bool IsPositive, bool IsDispellable,
    BattleStatusLifetime Lifetime, BattleStatusCounterKind CounterKind, BattleStatusMechanic Mechanic,
    BattleStatusSnapshotRefresh SnapshotRefresh, string? FamilyCode, BattleStatusFamilyRefresh FamilyRefresh,
    int InitialStacks, int MechanicLevel, decimal MechanicPower, bool IsHidden = false)
{
    public decimal FamilyStrength => Math.Abs(MechanicPower != 0 ? MechanicPower : ValuePerStack);
    internal static BattleStatusDefinition Compile(BattleStatusOptions status) => new(status.Code, status.Name,
        status.Description, status.EffectType, status.ValuePerStack, status.MaxStacks, status.Stacking,
        status.IsPositive, status.IsDispellable, status.Lifetime, status.CounterKind, status.Mechanic,
        status.SnapshotRefresh, status.FamilyCode, status.FamilyRefresh, status.InitialStacks, status.MechanicLevel, status.MechanicPower, status.IsHidden);
}
