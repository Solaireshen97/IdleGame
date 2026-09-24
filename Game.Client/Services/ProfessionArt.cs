namespace Game.Client.Services;

public static class ProfessionArt
{
    public static string ForCode(string? code) => code switch
    {
        "swordsman" => "/art/professions/swordsman.png",
        "acolyte" => "/art/professions/acolyte.png",
        "knight" => "/art/professions/knight.png",
        "warrior" => "/art/professions/warrior.png",
        "priest" => "/art/professions/priest.png",
        "mage" => "/art/professions/acolyte.png",
        "hunter" => "/art/professions/swordsman.png",
        "rogue" => "/art/professions/swordsman.png",
        "assassin" => "/art/professions/warrior.png",
        "trickster" => "/art/professions/acolyte.png",
        _ => "/art/professions/swordsman.png"
    };

    public static string ForName(string? name) => name switch
    {
        "剑士" => ForCode("swordsman"),
        "祭司" => ForCode("acolyte"),
        "骑士" => ForCode("knight"),
        "战士" => ForCode("warrior"),
        "牧师" => ForCode("priest"),
        "法师" => ForCode("mage"),
        "猎人" => ForCode("hunter"),
        "盗贼" => ForCode("rogue"),
        "刺客" => ForCode("assassin"),
        "诡术师" => ForCode("trickster"),
        _ => ForCode("swordsman")
    };
}
