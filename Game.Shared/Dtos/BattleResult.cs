namespace Game.Shared.Dtos;

public class BattleResult
{
    public int RoomId { get; set; }
    public int CharacterHp { get; set; }
    public int CharacterMaxHp { get; set; }
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public bool IsVictory { get; set; }
    public bool IsCharacterDead { get; set; }
    public List<string> Logs { get; set; } = new();
}
