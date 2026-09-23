using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class GatheringOpportunityService(GameDbContext db, GatheringCatalog catalog)
{
    public async Task<IReadOnlyList<(int CharacterId, string PointName)>> GrantForEliteAsync(
        string dungeonCode, IEnumerable<int> characterIds)
    {
        var ids = characterIds.Distinct().ToList();
        var points = catalog.Points.Where(point => point.IsRare &&
            string.Equals(point.UnlockTargetCode, dungeonCode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ids.Count == 0 || points.Count == 0) return [];

        var pointCodes = points.Select(point => point.Code).ToList();
        var saved = await db.CharacterGatheringOpportunities
            .Where(item => ids.Contains(item.CharacterId) && pointCodes.Contains(item.PointCode))
            .ToDictionaryAsync(item => (item.CharacterId, item.PointCode));
        var granted = new List<(int CharacterId, string PointName)>();
        foreach (var point in points)
        foreach (var characterId in ids)
        {
            var opportunity = db.CharacterGatheringOpportunities.Local.FirstOrDefault(item =>
                item.CharacterId == characterId && item.PointCode == point.Code)
                ?? saved.GetValueOrDefault((characterId, point.Code));
            if (opportunity is null)
                db.CharacterGatheringOpportunities.Add(new CharacterGatheringOpportunity
                {
                    CharacterId = characterId, PointCode = point.Code,
                    AvailableCount = 1, EarnedCount = 1
                });
            else
            {
                opportunity.AvailableCount = checked(opportunity.AvailableCount + 1);
                opportunity.EarnedCount = checked(opportunity.EarnedCount + 1);
                opportunity.Version++;
            }
            granted.Add((characterId, point.Name));
        }
        return granted;
    }
}
