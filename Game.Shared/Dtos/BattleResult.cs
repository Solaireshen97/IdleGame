namespace Game.Shared.Dtos;

using Game.Shared.Enums;

public class BattleResult
{
    public int RoomId { get; set; }
    public int CharacterHp { get; set; }
    public int CharacterMaxHp { get; set; }
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public int CurrentWaveNumber { get; set; }
    public int TotalWaveCount { get; set; }
    public RoomStatus RoomStatus { get; set; }
    public DateTime? NextRoundAvailableAtUtc { get; set; }
    public DateTime? BattleEndedAtUtc { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public bool CanExecuteRound { get; set; }
    public bool IsVictory { get; set; }
    public bool IsCharacterDead { get; set; }
    public List<string> Logs { get; set; } = new();
}
