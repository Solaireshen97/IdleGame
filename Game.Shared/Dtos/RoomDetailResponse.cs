using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class RoomDetailResponse
{
    public int RoomId { get; set; }
    public int OwnerUserId { get; set; }
    public int SlotCount { get; set; }
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public RoomStatus RoomStatus { get; set; }
    public DateTime? NextRoundAvailableAtUtc { get; set; }
    public DateTime? BattleEndedAtUtc { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public bool CanExecuteRound { get; set; }
    public bool CanStartPreparation { get; set; }
    public List<RoomSlotResponse> Slots { get; set; } = new();
}
