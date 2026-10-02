namespace Game.Shared.Dtos.Auth;

public class CurrentUserResponse
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int? ActiveCharacterId { get; set; }
    public int Gold { get; set; }
    public int CharacterCount { get; set; }
    public int CharacterSlotLimit { get; set; }
    public int MaximumCharacterSlots { get; set; }
    // Kept as null for clients from before all five slots became free.
    public int? NextCharacterSlotCost { get; set; }
}
