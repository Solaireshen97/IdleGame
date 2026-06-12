namespace Game.Shared.Dtos.Auth;

public class CharacterSummaryResponse
{
    public int CharacterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public bool IsCurrent { get; set; }
}
