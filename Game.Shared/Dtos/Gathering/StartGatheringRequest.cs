namespace Game.Shared.Dtos.Gathering;

public sealed class StartGatheringRequest
{
    public int CharacterId { get; set; }
    public string PointCode { get; set; } = string.Empty;
}
