namespace Game.Shared.Models;

public sealed class CharacterSoulImprint
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string SoulImprintCode { get; set; } = string.Empty;
    public int? EquippedSlotIndex { get; set; }
    public bool AutoUseEnabled { get; set; }
    public bool IsLocked { get; set; }
    public DateTime AcquiredAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}
