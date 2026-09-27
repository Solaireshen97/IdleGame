using Game.Server.Services;
using Xunit;

namespace Game.Server.Tests;

public class BattleLogStoreTests
{
    [Fact]
    public void LargeSettlementRetainsEveryHitAndDefeatMarker()
    {
        var store = new BattleLogStore();
        store.Append(1, ["上一回合"], DateTime.UtcNow);
        var settlement = Enumerable.Range(1, 100).Select(i => $"第 {i} 次命中")
            .Append("史莱姆 已被击败。").ToArray();
        store.Append(1, settlement, DateTime.UtcNow);
        Assert.Equal(settlement, store.Get(1).Select(log => log.Text));
        Assert.Equal(101, store.Get(1).Select(log => log.Id).Distinct().Count());
        store.Append(1, ["下一回合"], DateTime.UtcNow);
        Assert.Equal(80, store.Get(1).Count);
    }

    [Fact]
    public void ReplacingHistoryAlsoPreservesTheCompleteNewSettlement()
    {
        var store = new BattleLogStore();
        store.Append(1, ["旧场次"], DateTime.UtcNow);
        var settlement = Enumerable.Range(1, 100).Select(i => $"命中 {i}").ToArray();
        store.Replace(1, settlement, DateTime.UtcNow);
        Assert.Equal(settlement, store.Get(1).Select(log => log.Text));
    }
}
