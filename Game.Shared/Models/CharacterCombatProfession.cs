namespace Game.Shared.Models;

public sealed class CharacterCombatProfession
{
    public int CharacterId { get; set; }
    public string ProfessionCode { get; set; } = string.Empty;
    public int Level { get; set; } = 1;
    public int Experience { get; set; }
    public string? SkillLoadoutJson { get; set; }
}
