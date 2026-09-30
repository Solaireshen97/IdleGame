namespace Game.Shared.Dtos;

public sealed class RoomRewardsResponse
{
    public int RoomId { get; set; }
    public int RoomVersion { get; set; }
    public int RunSequence { get; set; }
    public RoomRewardSummaryResponse? Rewards { get; set; }
    public RoomCumulativeRewardsResponse? CumulativeRewards { get; set; }
}
