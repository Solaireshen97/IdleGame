namespace Game.Shared.Models;

public class Dungeon
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterMaxHp { get; set; }
    public int MonsterAttack { get; set; }
    public int MonsterDefense { get; set; }
    public int SlotCount { get; set; } = 5;
    public int SortOrder { get; set; }
}