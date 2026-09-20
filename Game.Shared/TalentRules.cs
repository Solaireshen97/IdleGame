using Game.Shared.Models;

namespace Game.Shared;

public static class TalentRules
{
    public const int MaxRank = 3;
    public const int AttackPerRank = 1;
    public const int DefensePerRank = 1;
    public const int HealthPerRank = 5;

    public static int NextRankCost(int currentRank) => currentRank + 1;
    public static int SpentPoints(int rank) => rank * (rank + 1) / 2;
    public static int EffectiveAttack(Character character) => character.Attack + character.AttackTalentRank * AttackPerRank;
    public static int EffectiveDefense(Character character) => character.Defense + character.DefenseTalentRank * DefensePerRank;
    public static int EffectiveMaxHp(Character character) => character.MaxHp + character.HealthTalentRank * HealthPerRank;
}
