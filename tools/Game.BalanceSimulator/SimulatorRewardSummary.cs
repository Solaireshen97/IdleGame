using Game.Shared.Models;

namespace Game.BalanceSimulator;

public static class SimulatorRewardSummary
{
    public static SimulatorRewards Build(IReadOnlyCollection<Character> actors,
        IReadOnlyCollection<RewardEntry> ledger, string settlementStatus)
    {
        var settled = settlementStatus is "Victory" or "Defeat";
        IReadOnlyCollection<RewardEntry> earnings = settled ? ledger : [];
        var characters = actors.OrderBy(actor => actor.Id).Select(actor => new SimulatorCharacterRewards(
            actor.Id, actor.UserId, Total([actor], earnings.Where(entry => entry.CharacterId == actor.Id)))).ToList();
        var accounts = actors.GroupBy(actor => actor.UserId).OrderBy(group => group.Key)
            .Select(group => new SimulatorAccountRewards(group.Key, group.Select(actor => actor.Id).Order().ToList(),
                Total(group, earnings.Where(entry => entry.UserId == group.Key)))).ToList();
        return new SimulatorRewards(settlementStatus, Total(actors, earnings), characters, accounts);
    }

    private static SimulatorRewardTotals Total(IEnumerable<Character> actors, IEnumerable<RewardEntry> entries)
    {
        var ledger = entries.ToList();
        var items = ledger.Where(entry => entry.RewardSource == "Base" &&
                (entry.EventKey == "clear" || entry.EventKey.StartsWith("monster:", StringComparison.Ordinal)) &&
                entry.Kind is "Consumable" or "Material" or "Weapon" or "SoulImprint")
            .GroupBy(entry => (entry.Kind, entry.Code)).OrderBy(group => group.Key.Kind, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Code, StringComparer.Ordinal)
            .Select(group => new SimulatorItemReward(group.Key.Kind, group.Key.Code, group.Sum(entry => entry.Quantity))).ToList();
        return new SimulatorRewardTotals(actors.Sum(actor => actor.Gold),
            ledger.Where(entry => entry.Kind == "Experience").Sum(entry => entry.Quantity),
            items.Sum(item => item.Quantity), items);
    }
}

public sealed record SimulatorRewards(string SettlementStatus, SimulatorRewardTotals Team,
    List<SimulatorCharacterRewards> Characters, List<SimulatorAccountRewards> Accounts);
public sealed record SimulatorRewardTotals(int Gold, int Experience, int OrdinaryItemQuantity, List<SimulatorItemReward> OrdinaryItems);
public sealed record SimulatorItemReward(string Kind, string Code, int Quantity);
public sealed record SimulatorCharacterRewards(int CharacterId, int UserId, SimulatorRewardTotals Earned);
public sealed record SimulatorAccountRewards(int UserId, List<int> CharacterIds, SimulatorRewardTotals Earned);
public sealed record SimulatorCoopRewardEvent(string EventKey, int CoopParticipantCount, decimal CoopDropBonusPercent);
