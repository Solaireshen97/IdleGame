namespace Game.Shared.Models;

public sealed class LogisticsRequest
{
    public int CharacterId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime CompletedAtUtc { get; set; }
}
