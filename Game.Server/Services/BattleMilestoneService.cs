using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class BattleMilestoneService(GameDbContext db)
{
    public const string MonsterKillKind = "MonsterKill";
    public const string DungeonClearKind = "DungeonClear";

    public async Task RecordAsync(IEnumerable<int> characterIds, string kind, string targetCode, DateTime now)
    {
        var ids = characterIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var saved = await db.CharacterBattleMilestones
            .Where(item => ids.Contains(item.CharacterId) && item.Kind == kind && item.TargetCode == targetCode)
            .ToDictionaryAsync(item => item.CharacterId);
        foreach (var characterId in ids)
        {
            var milestone = db.CharacterBattleMilestones.Local.FirstOrDefault(item =>
                item.CharacterId == characterId && item.Kind == kind && item.TargetCode == targetCode)
                ?? saved.GetValueOrDefault(characterId);
            if (milestone is null)
            {
                db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
                {
                    CharacterId = characterId, Kind = kind, TargetCode = targetCode,
                    Count = 1, FirstAtUtc = now, LastAtUtc = now
                });
            }
            else
            {
                milestone.Count = checked(milestone.Count + 1);
                milestone.LastAtUtc = now;
            }
        }
    }
}
