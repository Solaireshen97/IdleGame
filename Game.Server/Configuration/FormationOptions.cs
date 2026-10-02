namespace Game.Server.Configuration;

public sealed class FormationOptions
{
    public const string SectionName = "Formations";
    public int PositionsPerElement { get; set; } = 6;
    public int ValidatedPositions => PositionsPerElement is >= 1 and <= 20 ? PositionsPerElement
        : throw new InvalidOperationException("Formation positions per element must be between 1 and 20.");
}
