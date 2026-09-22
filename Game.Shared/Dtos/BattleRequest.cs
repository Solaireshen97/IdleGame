namespace Game.Shared.Dtos;

public class BattleRequest
{
    public int RoomId { get; set; }
    public int? ExpectedRoundNumber { get; set; }
}
