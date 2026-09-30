namespace Game.Shared.Models;

/// <summary>Room-owned rules, stored separately from frequently synchronized room state.</summary>
public sealed class DungeonRunRuleSnapshot
{
    public int RoomId { get; set; }
    public string Revision { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
}
