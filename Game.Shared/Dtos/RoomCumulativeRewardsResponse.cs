namespace Game.Shared.Dtos;

public sealed class RoomCumulativeRewardsResponse
{
    public int CompletedRuns { get; set; }
    public int Gold { get; set; }
    public int Experience { get; set; }
    public List<RoomRewardItemResponse> Items { get; set; } = [];
}
