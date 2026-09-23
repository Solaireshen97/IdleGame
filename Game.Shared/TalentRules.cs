using Game.Shared.Models;
using Game.Shared.Enums;

namespace Game.Shared;

public static class TalentRules
{
    public const int MaxRank = 3;
    public const int AttackPerRank = 1;
    public const int HealthPerRank = 5;

    public static int NextRankCost(int currentRank) => currentRank + 1;
    public static int SpentPoints(int rank) => rank * (rank + 1) / 2;
    public static int GetRank(Character character, TalentType type) => type switch
    {
        TalentType.Attack => character.AttackTalentRank,
        TalentType.Health => character.HealthTalentRank,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    public static int EffectiveAttack(Character character) => character.Attack;
    public static int EffectiveMaxHp(Character character)
    {
        var weaponHp = decimal.Floor(character.MaxHp *
            (1m + (character.WeaponHealthBonusPercent + character.TemporaryWeaponHealthBonusPercent) / 100m) *
            (1m + character.TalentMaxHpPercent / 100m));
        return (int)Math.Min(int.MaxValue, Math.Max(1m, weaponHp));
    }
}
