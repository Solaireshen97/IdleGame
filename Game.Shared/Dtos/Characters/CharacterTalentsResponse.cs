using Game.Shared.Enums;

namespace Game.Shared.Dtos.Characters;

public class CharacterTalentsResponse
{
    public int CharacterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public int TalentPoints { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public List<TalentNodeResponse> Talents { get; set; } = [];
}

public class TalentNodeResponse
{
    public TalentType Type { get; set; }
    public int Rank { get; set; }
    public int MaxRank { get; set; }
    public int BonusPerRank { get; set; }
    public int? NextRankCost { get; set; }
}
