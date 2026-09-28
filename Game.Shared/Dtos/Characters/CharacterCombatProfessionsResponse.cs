namespace Game.Shared.Dtos.Characters;

public sealed class CharacterCombatProfessionsResponse
{
    public int CharacterId { get; set; }
    public string ActiveProfessionCode { get; set; } = string.Empty;
    public bool CanSwitch { get; set; }
    public List<CharacterCombatProfessionResponse> Professions { get; set; } = [];
}

public sealed class CharacterCombatProfessionResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Level { get; set; }
    public int Experience { get; set; }
    public int? ExperienceToNextLevel { get; set; }
    public bool IsActive { get; set; }
    public bool IsSharedSkillUnlocked { get; set; }
}

public sealed class SwitchCombatProfessionRequest
{
    public string ProfessionCode { get; set; } = string.Empty;
}
