namespace Game.Shared.Models;

/// <summary>Encounter-local phase state, committed atomically with the combat round.</summary>
public sealed class BattleMonsterPhaseState
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int MonsterId { get; set; }
    public int EncounterStartRound { get; set; }
    public int LastPreparedRound { get; set; } = -1;
    public int NextActivationRound { get; set; }
    public bool IsActive { get; set; }
    public int ExpiresAfterRound { get; set; }
    public long WaterDamage { get; set; }
    public int? RewardStartsAtRound { get; set; }
    public int? LastActivationRound { get; set; }
    public int? LastBreakRound { get; set; }
    public int? LastExpiryRound { get; set; }
    public int ActivationCount { get; set; }
    public int BreakCount { get; set; }
    public int ExpiryCount { get; set; }
    public int LinkedHitCount { get; set; }
}
