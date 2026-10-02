namespace Game.Shared.Enums;

/// <summary>The damage calculations to which a status modifier applies.</summary>
[Flags]
public enum BattleDamageScope
{
    Direct = 1,
    Periodic = 2,
    All = Direct | Periodic
}
