namespace Game.Shared.Models;

public sealed class CharacterProfessionTalent
{
    public int CharacterId { get; set; }
    public string ProfessionCode { get; set; } = string.Empty;
    public string NodeCode { get; set; } = string.Empty;
    public int Rank { get; set; }
}
