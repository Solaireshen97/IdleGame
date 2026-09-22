namespace Game.Server.Configuration;

public sealed class CharacterSlotOptions
{
    public const string SectionName = "CharacterSlots";

    public int InitialSlots { get; set; } = 2;
    public int MaximumSlots { get; set; } = 5;
    public List<int> UnlockCosts { get; set; } = [];
}
