using Game.Shared.Enums;

namespace Game.Shared.Models;

public class Monster
{
    public int Id { get; set; }
    public int? RoomId { get; set; }
    public int WaveNumber { get; set; } = 1;
    public int Position { get; set; } = 1;
    public string CombatProfileCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; } = ElementType.Wind;
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
}
