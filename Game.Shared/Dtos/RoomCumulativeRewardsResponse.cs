namespace Game.Shared.Dtos;

public sealed class RoomCumulativeRewardsResponse
{
    public int CompletedRuns { get; set; }
    public int Gold { get; set; }
    public int MasteryGold { get; set; }
    public int Experience { get; set; }
    public bool HasPendingRewards { get; set; }
    public List<RoomRewardItemResponse> Items { get; set; } = [];
    public List<RoomCharacterRewardsResponse> Characters { get; set; } = [];
}

public sealed class RoomCharacterRewardsResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public int Gold { get; set; }
    public int Experience { get; set; }
    public int PendingGold { get; set; }
    public int PendingExperience { get; set; }
    public List<RoomRewardItemResponse> Items { get; set; } = [];
}
