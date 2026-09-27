using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class DungeonDepthProgressService(GameDbContext db, DungeonDepthCatalog? catalog = null)
{
    public async Task<DungeonDepthDefinitionOptions?> DefinitionAsync(Room room)
    {
        if (room.DepthDefinitionJson is not null)
            return JsonSerializer.Deserialize<DungeonDepthDefinitionOptions>(room.DepthDefinitionJson);
        var dungeon = await db.Dungeons.FindAsync(room.DungeonId);
        var definition = dungeon is null ? null : catalog?.Find(dungeon.Code);
        // An upgraded LV1 room freezes its rules on the next successful battle save.
        if (definition is not null) room.DepthDefinitionJson = JsonSerializer.Serialize(definition);
        return definition;
    }

    public async Task<int> HighestAsync(int characterId, int dungeonId)
    {
        var saved = await db.CharacterDungeonProgress.FindAsync(characterId, dungeonId);
        if (saved is not null) return saved.HighestDepth;
        var dungeon = await db.Dungeons.FindAsync(dungeonId);
        if (dungeon is null) return 0;
        return await db.CharacterBattleMilestones.AnyAsync(item => item.CharacterId == characterId &&
            item.Kind == BattleMilestoneService.DungeonClearKind && item.TargetCode == dungeon.Code && item.Count > 0) ? 1 : 0;
    }

    public async Task<int> UnlockedAsync(int userId, int dungeonId, int maximumDepth)
    {
        var clear = db.UserDungeonClears.Local.FirstOrDefault(item => item.UserId == userId && item.DungeonId == dungeonId)
            ?? await db.UserDungeonClears.SingleOrDefaultAsync(item => item.UserId == userId && item.DungeonId == dungeonId);
        return Math.Min(maximumDepth, (clear?.HighestDepth ?? 0) + 1);
    }

    public async Task<string?> AdmissionErrorAsync(int userId, Dungeon dungeon, int depth)
    {
        var definition = catalog?.Find(dungeon.Code);
        if (depth < 1 || depth > (definition?.MaximumDepth ?? 1)) return "InvalidDungeonDepth";
        return depth > await UnlockedAsync(userId, dungeon.Id, definition?.MaximumDepth ?? 1)
            ? "DungeonDepthLocked" : null;
    }

    public async Task CaptureAsync(Room room, IEnumerable<int> characterIds)
    {
        if (await DefinitionAsync(room) is null) return;
        foreach (var id in characterIds.Distinct())
        {
            var existing = await db.DungeonRunParticipants.FindAsync(room.Id, room.RunSequence, id);
            if (existing is not null) continue;
            db.DungeonRunParticipants.Add(new DungeonRunParticipant
            {
                RoomId = room.Id, RunSequence = room.RunSequence, CharacterId = id,
                MasteryLevel = Math.Min(4, await HighestAsync(id, room.DungeonId))
            });
        }
    }

    public async Task<int> RunMasteryAsync(Room room, int characterId)
    {
        await CaptureAsync(room, [characterId]);
        return (await db.DungeonRunParticipants.FindAsync(room.Id, room.RunSequence, characterId))?.MasteryLevel ?? 0;
    }

    public async Task RecordCharacterClearsAsync(Room room, IEnumerable<int> characterIds, List<string> logs)
    {
        if (await DefinitionAsync(room) is null) return;
        foreach (var id in characterIds.Distinct())
        {
            var progress = await db.CharacterDungeonProgress.FindAsync(id, room.DungeonId);
            var previous = progress?.HighestDepth ?? await HighestAsync(id, room.DungeonId);
            if (progress is null)
            {
                progress = new CharacterDungeonProgress { CharacterId = id, DungeonId = room.DungeonId,
                    HighestDepth = Math.Max(previous, room.DepthLevel) };
                db.CharacterDungeonProgress.Add(progress);
            }
            else if (room.DepthLevel > progress.HighestDepth)
            {
                progress.HighestDepth = room.DepthLevel;
                progress.Version++;
            }
            if (room.DepthLevel > previous)
            {
                var character = await db.Characters.FindAsync(id);
                var mastery = Math.Min(4, room.DepthLevel);
                logs.Add(mastery > Math.Min(4, previous)
                    ? $"{character?.Name ?? "角色"} 的副本精通提升至 LV{mastery}，新收益从下轮生效。"
                    : $"{character?.Name ?? "角色"} 刷新挑战纪录：深层 LV{room.DepthLevel}（精通已满）。");
            }
        }
    }
}
