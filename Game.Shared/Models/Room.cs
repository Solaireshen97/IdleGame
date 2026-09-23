using Game.Shared.Enums;

namespace Game.Shared.Models;

public class Room
{
    public int Id { get; set; }
    public int DungeonId { get; set; }
    public int MonsterId { get; set; }
    public int OwnerUserId { get; set; }
    public int SlotCount { get; set; }
    public RoomStatus Status { get; set; }
    public DateTime? NextRoundAvailableAtUtc { get; set; }
    public int? RoundCooldownDurationSeconds { get; set; }
    public DateTime? PreparationStartedAtUtc { get; set; }
    public DateTime? BattleEndedAtUtc { get; set; }
    public bool IsPreparationTimeoutEnabled { get; set; } = true;
    public bool IsRepeatBattle { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public int RoundNumber { get; set; }
    public int RunSequence { get; set; } = 1;
    public int CurrentWaveNumber { get; set; } = 1;
    public int TotalWaveCount { get; set; } = 1;
    public int Version { get; set; }
}
