namespace Game.Shared.Models;

public sealed class CharacterSkillTalent
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string NodeCode { get; set; } = string.Empty;
    public int PointsSpent { get; set; }
}
