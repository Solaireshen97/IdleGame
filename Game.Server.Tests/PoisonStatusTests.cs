using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task ActivePoisonRefreshFromAnAllyCannotShortenExtendedPoison()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (_, monsterCombat) = CreateProfessionBalanceService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 5, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();
        test.Room.RoundNumber = 1;

        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 3, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var poison = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal(5, poison.ExpiresAfterRound);
        Assert.Equal(0, poison.AppliedRound);
        Assert.Equal(2, poison.Stacks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PoisonAfterCleansingOrExpiryUsesTheNewApplicationsDuration(bool cleanse)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (_, monsterCombat) = CreateProfessionBalanceService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 5, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();
        if (cleanse)
            await monsterCombat.RemoveFirstStatusAsync(test.Room, "Monster", [test.Monster.Id], false);
        else
            test.Room.RoundNumber = 6;

        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 3, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var poison = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal(test.Room.RoundNumber + 3, poison.ExpiresAfterRound);
        Assert.Equal(test.Room.RoundNumber, poison.AppliedRound);
        Assert.Equal(1, poison.Stacks);
    }
}
