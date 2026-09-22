namespace Game.Shared.Dtos.Characters;

public class CreateCharacterRequest
{
    public string Name { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = "swordsman";
}
