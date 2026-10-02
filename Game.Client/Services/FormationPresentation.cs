namespace Game.Client.Services;

public static class FormationPresentation
{
    public static string SectionName(string section) => section switch
    {
        "Profession" => "职业",
        "Weapons" => "武器盘",
        "Skills" => "技能",
        "Consumables" => "补给",
        "SoulImprint" or "SoulImprints" => "魂印",
        _ => "配置"
    };
}
