using Game.Shared.Enums;

namespace Game.Shared.Models;

public class RoomOperation
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int UserId { get; set; }
    public RoomOperationKind Kind { get; set; }
    public int SlotIndex { get; set; }
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public int? ExpectedTargetCharacterId { get; set; }
    public int? ExpectedSourceSlotIndex { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int Version { get; set; }
    public int? SourceFormationId { get; set; }
    public int? SourceFormationVersion { get; set; }
    public string? SourceFormationName { get; set; }
    public string? RequestedLoadoutJson { get; set; }
    public bool RememberForEncounter { get; set; }
    public string? RequestId { get; set; }
    public string? RequestFingerprint { get; set; }
}
