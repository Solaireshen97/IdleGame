namespace Game.Shared.Models;

public class UserDungeonClear
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int DungeonId { get; set; }
    public int HighestDepth { get; set; } = 1;
    public int Version { get; set; }
    public DateTime ClearedAtUtc { get; set; }
}
