using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class RoomListQuery(GameDbContext db, DungeonDepthCatalog depths)
{
    public async Task<List<RoomSummaryResponse>> ReadAsync(int? currentUserId,
        CancellationToken cancellationToken = default)
    {
        // Keep visibility and all per-room facts in the same database statement.
        // A reserved slot grants visibility, while participation still requires a
        // character in an open room or a retained slot in a closed room.
        var rows = await (from room in db.Rooms.AsNoTracking()
            join monster in db.Monsters on room.MonsterId equals monster.Id
            join dungeon in db.Dungeons on room.DungeonId equals dungeon.Id into dungeons
            from dungeon in dungeons.DefaultIfEmpty()
            where room.ClosedAtUtc == null && room.IsPublic ||
                currentUserId.HasValue && (room.OwnerUserId == currentUserId.Value ||
                    db.RoomSlots.Any(slot => slot.RoomId == room.Id && slot.UserId == currentUserId.Value))
            orderby room.ClosedAtUtc != null, room.Id descending
            select new SummaryRow
            {
                Room = room, Monster = monster, Dungeon = dungeon,
                IsParticipant = currentUserId.HasValue && db.RoomSlots.Any(slot =>
                    slot.RoomId == room.Id && slot.UserId == currentUserId.Value &&
                    (slot.CharacterId.HasValue || room.ClosedAtUtc.HasValue)),
                EnemiesInCurrentWave = monster.RoomId.HasValue ? db.Monsters.Count(candidate =>
                    candidate.RoomId == room.Id && candidate.WaveNumber == monster.WaveNumber) : 1,
                PendingOperationCount = currentUserId.HasValue ? db.RoomOperations.Count(operation =>
                    operation.RoomId == room.Id && operation.UserId == currentUserId.Value && operation.Status == "Pending") : 0
            }).ToListAsync(cancellationToken);
        return rows.Select(row => BuildSummary(row, currentUserId)).ToList();
    }

    private RoomSummaryResponse BuildSummary(SummaryRow row, int? currentUserId)
    {
        var room = row.Room;
        var monster = row.Monster;
        var dungeon = row.Dungeon;
        return new RoomSummaryResponse
        {
            RoomId = room.Id, MonsterName = monster.Name, MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp,
            RegionCode = dungeon?.RegionCode ?? "", RegionName = dungeon?.RegionName ?? "",
            DepthLevel = room.DepthLevel,
            DungeonName = dungeon is not null && (depths.Find(dungeon.Code) is not null || room.DepthDefinitionJson is not null)
                ? depths.DisplayName(dungeon.Name, room.DepthLevel) : dungeon?.Name ?? "",
            CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount,
            CurrentEnemyNumber = monster.Position, EnemiesInCurrentWave = row.EnemiesInCurrentWave,
            RoomStatus = room.Status, IsRepeatBattle = room.IsRepeatBattle, IsPublic = room.IsPublic,
            ExpiresAtUtc = room.ExpiresAtUtc, ClosedAtUtc = room.ClosedAtUtc,
            IsPreparationTimeoutEnabled = room.IsPreparationTimeoutEnabled,
            IsCurrentUserParticipant = row.IsParticipant,
            PendingOperationCount = row.PendingOperationCount,
            IsOwnedByCurrentUser = currentUserId.HasValue && room.OwnerUserId == currentUserId.Value
        };
    }

    private sealed class SummaryRow
    {
        public required Room Room { get; init; }
        public required Monster Monster { get; init; }
        public Dungeon? Dungeon { get; init; }
        public bool IsParticipant { get; init; }
        public int EnemiesInCurrentWave { get; init; }
        public int PendingOperationCount { get; init; }
    }
}
