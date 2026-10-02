using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class CampaignAccessService(GameDbContext db, DungeonDepthCatalog? depths = null)
{
    // Missing state is a compatibility path for imported/pre-campaign accounts. Registration always creates it.
    public async Task<HashSet<string>?> UnlocksAsync(int userId)
    {
        var state = db.UserStoryStates.Local.FirstOrDefault(x => x.UserId == userId) ?? await db.UserStoryStates.FindAsync(userId);
        if (state is null || state.IsLegacy) return null;
        return (await db.StoryMapUnlocks.Where(x => x.UserId == userId).Select(x => x.MapNodeCode).ToListAsync())
            .Concat(db.StoryMapUnlocks.Local.Where(x => x.UserId == userId).Select(x => x.MapNodeCode)).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<string?> AdmissionErrorAsync(int userId, Dungeon dungeon)
    {
        var unlocked = await UnlocksAsync(userId);
        if (unlocked is null || unlocked.Contains(dungeon.Code)) return null;
        // A real ordinary clear already opens its depths. Story turn-in must not revoke that existing right.
        var prerequisite = depths?.Find(dungeon.Code)?.PrerequisiteDungeonCode;
        if (prerequisite is not null && await (from clear in db.UserDungeonClears
                join parent in db.Dungeons on clear.DungeonId equals parent.Id
                where clear.UserId == userId && parent.Code == prerequisite && parent.RegionCode == dungeon.RegionCode
                select clear.Id).AnyAsync()) return null;
        return "StoryMapLocked";
    }
}
