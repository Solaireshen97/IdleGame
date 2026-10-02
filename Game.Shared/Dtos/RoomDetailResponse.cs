using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class RoomDetailResponse
{
    public string? LoadoutIntegrityError { get; set; }
    public int RoomId { get; set; }
    public int RoomVersion { get; set; }
    public string BattleHistoryEpoch { get; set; } = "";
    public int OwnerUserId { get; set; }
    public int DungeonId { get; set; }
    public bool SupportsDepths { get; set; }
    public int DepthLevel { get; set; } = 1;
    public int MasteryLevel { get; set; }
    public int CharacterHighestDepth { get; set; }
    public int UnlockedDepth { get; set; } = 1;
    public string DungeonName { get; set; } = string.Empty;
    public bool IsPartyHpScaled { get; set; }
    public int ScalingPartySize { get; set; } = 1;
    public int MonsterHpPercent { get; set; } = 100;
    public int MonsterBaseMaxHp { get; set; }
    public string RegionName { get; set; } = string.Empty;
    public int SlotCount { get; set; }
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterId { get; set; }
    public ElementType MonsterElement { get; set; }
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public int CurrentWaveNumber { get; set; }
    public int TotalWaveCount { get; set; }
    public int CurrentEnemyNumber { get; set; }
    public int EnemiesInCurrentWave { get; set; }
    public RoomStatus RoomStatus { get; set; }
    public int RunSequence { get; set; }
    public int RoundNumber { get; set; }
    public DateTime? NextRoundAvailableAtUtc { get; set; }
    public int? RoundCooldownDurationSeconds { get; set; }
    public DateTime? PreparationStartedAtUtc { get; set; }
    public DateTime? PreparationExpiresAtUtc { get; set; }
    public DateTime? BattleEndedAtUtc { get; set; }
    public DateTime? NextBattleStartAtUtc { get; set; }
    public DateTime? NextWaveStartAtUtc { get; set; }
    public bool IsRepeatBattle { get; set; }
    public bool IsPublic { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public bool CanExecuteRound { get; set; }
    public bool IsMixedTeam { get; set; }
    public bool IsPreparationTimeoutEnabled { get; set; }
    public int PreparationTimeoutSeconds { get; set; }
    public bool IsCurrentUserAutoUnlocked { get; set; }
    public bool IsCurrentUserAutoEnabled { get; set; }
    public bool IsAllAliveMembersAuto { get; set; }
    public bool CanPrepare { get; set; }
    public bool CanCancelPreparation { get; set; }
    public bool CanLeaveRoom { get; set; }
    public MonsterIntentResponse? MonsterIntent { get; set; }
    public List<BattleStatusEffectResponse> MonsterEffects { get; set; } = [];
    public List<RoomSlotResponse> Slots { get; set; } = new();
    public List<RoomOperationResponse> Operations { get; set; } = [];
    public RoomRewardSummaryResponse? Rewards { get; set; }
    public CoopDropBonusPreviewResponse? CoopDropBonus { get; set; }
    public decimal DirectDamageVariancePercent { get; set; }
    public RoomCumulativeRewardsResponse? CumulativeRewards { get; set; }
    public List<BattleLogResponse> BattleLogs { get; set; } = [];
    public List<BattleEventResponse> BattleEvents { get; set; } = [];

    // The unchanged protocol replaces only history/time. Keep the previous view's
    // scalar state and collections, without copying potentially large reward data.
    public RoomDetailResponse WithBattleHistory(BattleUnchangedResponse history)
    {
        var copy = (RoomDetailResponse)MemberwiseClone();
        copy.ServerTimeUtc = history.ServerTimeUtc;
        copy.BattleHistoryEpoch = history.BattleHistoryEpoch;
        copy.BattleEvents = history.BattleEvents;
        copy.BattleLogs = history.BattleLogs;
        return copy;
    }
}

public class BattleLogResponse
{
    public long Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
