using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class RoomSummaryResponse
{
    public int RoomId { get; set; }
    public string RegionCode { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string DungeonName { get; set; } = string.Empty;
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public int CurrentWaveNumber { get; set; }
    public int TotalWaveCount { get; set; }
    public int CurrentEnemyNumber { get; set; }
    public int EnemiesInCurrentWave { get; set; }
    public RoomStatus RoomStatus { get; set; }
    public bool IsRepeatBattle { get; set; }
    public bool IsPublic { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public bool IsPreparationTimeoutEnabled { get; set; }
    public bool IsCurrentUserParticipant { get; set; }
    public bool IsOwnedByCurrentUser { get; set; }
}
