namespace Game.Shared.Dtos;

public class QueueSkillRequest
{
    public int RoomId { get; set; }
    public int CharacterId { get; set; }
    public int SkillSlotIndex { get; set; }
    public bool IsQueued { get; set; }
}
