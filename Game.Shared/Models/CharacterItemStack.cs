namespace Game.Shared.Models;

public class CharacterItemStack
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int Version { get; set; }
}
