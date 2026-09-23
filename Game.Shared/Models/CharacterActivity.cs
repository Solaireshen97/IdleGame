namespace Game.Shared.Models;

public sealed class CharacterActivity
{
    public int CharacterId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int SourceId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
}
