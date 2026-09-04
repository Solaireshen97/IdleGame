namespace Game.Shared.Dtos;

public class CreateRoomRequest
{
    public int? DungeonId { get; set; }
    public string? MonsterType { get; set; }
}
