namespace Game.Shared.Dtos;

public sealed class RoomRewardSummaryResponse
{
    public int RunSequence { get; set; }
    public bool IsCurrentRun { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? SettledAtUtc { get; set; }
    public int Gold { get; set; }
    public int Experience { get; set; }
    public List<RoomRewardItemResponse> Items { get; set; } = [];
}

public sealed class RoomRewardItemResponse
{
    public string CharacterName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int Quantity { get; set; }
}
