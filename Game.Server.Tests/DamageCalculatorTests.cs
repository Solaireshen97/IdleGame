using Game.Server.Services;
using Game.Shared.Enums;
using Xunit;

namespace Game.Server.Tests;

public class DamageCalculatorTests
{
    [Theory]
    [InlineData(ElementType.Fire, ElementType.Wind, 25, -25)]
    [InlineData(ElementType.Wind, ElementType.Earth, 25, -25)]
    [InlineData(ElementType.Earth, ElementType.Water, 25, -25)]
    [InlineData(ElementType.Water, ElementType.Fire, 25, -25)]
    [InlineData(ElementType.Fire, ElementType.Water, -25, 25)]
    [InlineData(ElementType.Light, ElementType.Dark, 25, -25)]
    [InlineData(ElementType.Dark, ElementType.Light, 25, -25)]
    [InlineData(ElementType.Light, ElementType.Light, 0, 0)]
    [InlineData(ElementType.Fire, ElementType.Light, 0, 0)]
    public void MatchupAppliesToBothOutgoingAndIncomingDamage(
        ElementType player, ElementType monster, int outgoing, int incoming)
    {
        Assert.Equal(outgoing, ElementMatchup.PlayerAttackPercent(player, monster));
        Assert.Equal(incoming, ElementMatchup.MonsterAttackPercent(monster, player));
    }

    [Fact]
    public void NoMainWeaponIsNeutral()
    {
        Assert.Equal(0, ElementMatchup.PlayerAttackPercent(null, ElementType.Wind));
        Assert.Equal(0, ElementMatchup.MonsterAttackPercent(ElementType.Wind, null));
    }

    [Fact]
    public void ZonesMultiplyAfterAttackAndDefenseAndRoundDownOnce()
    {
        var damage = DamageCalculator.Calculate(20, 5, skillPower: 8,
            factors: new DamageFactors(AttackPercent: 10, HealthPercent: 20,
                CriticalPercent: 50, ElementPercent: 25, ReductionPercent: 50));

        Assert.Equal(29, damage);
        Assert.Equal(1, DamageCalculator.Calculate(1, 100, factors: new DamageFactors(ElementPercent: -25, ReductionPercent: 75)));
    }

    [Fact]
    public void NeutralDamagePreservesExistingAttackSkillAndGuardValues()
    {
        Assert.Equal(15, DamageCalculator.Calculate(20, 5));
        Assert.Equal(23, DamageCalculator.Calculate(20, 5, skillPower: 8));
        Assert.Equal(3, DamageCalculator.Calculate(12, 5, factors: new DamageFactors(ReductionPercent: 50)));
    }

    [Fact]
    public void NegativeReductionActsAsVulnerability()
    {
        Assert.Equal(18, DamageCalculator.Calculate(20, 5,
            factors: new DamageFactors(ReductionPercent: -20)));
    }
}
