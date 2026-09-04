namespace Game.Shared.Dtos;

public class DungeonSummaryResponse
{
    public int DungeonId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterMaxHp { get; set; }
    public int MonsterAttack { get; set; }
    public int MonsterDefense { get; set; }
    public int SlotCount { get; set; }
    public bool IsClearedByCurrentUser { get; set; }
    public bool AutoUnlocked { get; set; }
}