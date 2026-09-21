namespace Game.Shared.Models;

public class Character
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = "knight";
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public decimal WeaponAttackBonusPercent { get; set; }
    public decimal WeaponHealthBonusPercent { get; set; }
    public decimal WeaponCriticalChancePercent { get; set; }
    public int Level { get; set; } = 1;
    public int Experience { get; set; }
    public int TalentPoints { get; set; }
    public int AttackTalentRank { get; set; }
    public int DefenseTalentRank { get; set; }
    public int HealthTalentRank { get; set; }
    public int Version { get; set; }
}
