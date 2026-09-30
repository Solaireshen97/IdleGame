namespace Game.Shared.Dtos;

public sealed class BattleSyncRequest
{
    public int RoomId { get; set; }
    public string HistoryEpoch { get; set; } = "";
    public long AfterEventId { get; set; }
    public long AfterLogId { get; set; }
    public string ProjectionId { get; set; } = "";
}

public sealed class BattleSyncResponse
{
    public RoomDetailResponse? Room { get; set; }
    public BattleUnchangedResponse? Unchanged { get; set; }
    public string ProjectionId { get; set; } = "";
    public bool HistoryReset { get; set; }
    public long LastEventId { get; set; }
    public long LastLogId { get; set; }
}

public sealed class BattleUnchangedResponse
{
    public int RoomId { get; set; }
    public int RoomVersion { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public string BattleHistoryEpoch { get; set; } = "";
    public List<BattleEventResponse> BattleEvents { get; set; } = [];
    public List<BattleLogResponse> BattleLogs { get; set; } = [];
}
