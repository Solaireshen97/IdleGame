namespace Game.Shared.Dtos;

public class CreateRoomRequest
{
    public int? DungeonId { get; set; }
    public string? MonsterType { get; set; }
    public bool IsRepeatBattle { get; set; }
    public bool IsPreparationTimeoutEnabled { get; set; } = true;
    public bool IsPublic { get; set; }
}
