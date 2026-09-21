using Game.Shared.Enums;

namespace Game.Shared.Models;

public class Monster
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; } = ElementType.Wind;
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
}
