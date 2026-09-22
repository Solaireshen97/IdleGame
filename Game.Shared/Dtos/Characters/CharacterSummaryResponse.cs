namespace Game.Shared.Dtos.Characters;

public class CharacterSummaryResponse
{
    public int CharacterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = string.Empty;
    public string ProfessionName { get; set; } = string.Empty;
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Level { get; set; }
    public int Experience { get; set; }
    public int? ExperienceToNextLevel { get; set; }
    public int TalentPoints { get; set; }
    public bool IsCurrent { get; set; }
}
