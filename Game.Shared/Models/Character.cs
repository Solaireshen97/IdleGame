namespace Game.Shared.Models;

public class Character
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = "swordsman";
    public string? AdvancedProfessionCode { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public decimal WeaponAttackBonusPercent { get; set; }
    public decimal WeaponHealthBonusPercent { get; set; }
    public decimal WeaponCriticalChancePercent { get; set; }
    public decimal WeaponStaminaPercent { get; set; }
    public decimal WeaponEnmityPercent { get; set; }
    public decimal WeaponDoubleAttackChancePercent { get; set; }
    public decimal WeaponNormalEchoPercent { get; set; }
    public decimal WeaponSkillDamagePercent { get; set; }
    public int Level { get; set; } = 1;
    public int GatheringLevel { get; set; } = 1;
    public int AlchemyLevel { get; set; } = 1;
    public int Experience { get; set; }
    public int TalentPoints { get; set; }
    public int AttackTalentRank { get; set; }
    public int HealthTalentRank { get; set; }
    public decimal TalentMaxHpPercent { get; set; }
    public decimal TalentNormalAttackPercent { get; set; }
    public decimal TalentSkillDamagePercent { get; set; }
    public decimal TalentHealingDonePercent { get; set; }
    public decimal TalentHealingReceivedPercent { get; set; }
    public decimal TalentSkillCriticalChancePercent { get; set; }
    public int Version { get; set; }
}
