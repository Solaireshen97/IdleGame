namespace Game.Shared.Dtos.Auth;

public class CurrentUserResponse
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int Gold { get; set; }
    public int CharacterCount { get; set; }
    public int CharacterSlotLimit { get; set; }
    public int MaximumCharacterSlots { get; set; }
    public int? NextCharacterSlotCost { get; set; }
}
