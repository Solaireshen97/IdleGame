namespace Game.Shared.Dtos;

public class SetSlotAutoResponse
{
    public RoomDetailResponse Room { get; set; } = new();
    public BattleResult? RoundResult { get; set; }
}