namespace Game.Server.Configuration;

public sealed class MaterialOptions
{
    public const string SectionName = "Materials";
    public List<MaterialItemOptions> Items { get; set; } = [];
}

public sealed class MaterialItemOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
