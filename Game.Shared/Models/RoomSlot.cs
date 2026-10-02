namespace Game.Shared.Models;

public class RoomSlot
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int SlotIndex { get; set; }
    public int? CharacterId { get; set; }
    public int? UserId { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public bool IsConfirmed { get; set; }
    public bool IsAutoEnabled { get; set; }
    public bool IsTemporaryAuto { get; set; }
    public int PendingConsumableSlotMask { get; set; }
    public int PendingSkillSlotMask { get; set; }
    public string? PendingSkillTargetsJson { get; set; }
    public bool IsSoulImprintQueued { get; set; }
    public bool HasParticipatedInRun { get; set; }
    public int? LastParticipatedMonsterId { get; set; }
    public int? SourceFormationId { get; set; }
    public int? SourceFormationVersion { get; set; }
    public string? SourceFormationName { get; set; }
    public string? AppliedLoadoutJson { get; set; }
    public string? AutoPolicyOverridesJson { get; set; }
}
