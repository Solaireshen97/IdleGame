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
    public DateTime? PreparationStartedAtUtc { get; set; }
    public DateTime? PreparationExpiresAtUtc { get; set; }
    public DateTime? BattleEndedAtUtc { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public bool CanExecuteRound { get; set; }
    public bool IsMixedTeam { get; set; }
    public bool IsAllAliveMembersAuto { get; set; }
    public bool CanPrepare { get; set; }
    public bool CanLeaveRoom { get; set; }
    public List<RoomSlotResponse> Slots { get; set; } = new();
}
