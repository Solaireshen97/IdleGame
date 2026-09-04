using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class RoomService(GameDbContext dbContext, UserService userService)
{
    private const int SlotCount = 5;

    public async Task<List<RoomSummaryResponse>> GetRoomsAsync()
    {
        var rooms = await dbContext.Rooms.ToListAsync();
        var result = new List<RoomSummaryResponse>();
        foreach (var room in rooms)
        {
            var summary = await BuildRoomSummaryAsync(room);
            if (summary is not null) result.Add(summary);
        }
        return result;
    }

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId, string? token = null)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return null;
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        return await BuildRoomDetailAsync(room, error is null ? user!.Id : null);
    }

    public async Task<List<DungeonSummaryResponse>> GetDungeonsAsync(string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        var clearedDungeonIds = error is null
            ? await dbContext.UserDungeonClears.Where(clear => clear.UserId == user!.Id).Select(clear => clear.DungeonId).ToListAsync()
            : [];
        return (await dbContext.Dungeons.OrderBy(dungeon => dungeon.SortOrder).ToListAsync()).Select(dungeon => new DungeonSummaryResponse
        {
            DungeonId = dungeon.Id, Code = dungeon.Code, Name = dungeon.Name, MonsterName = dungeon.MonsterName,
            MonsterMaxHp = dungeon.MonsterMaxHp, MonsterAttack = dungeon.MonsterAttack, MonsterDefense = dungeon.MonsterDefense,
            SlotCount = dungeon.SlotCount, IsClearedByCurrentUser = clearedDungeonIds.Contains(dungeon.Id), AutoUnlocked = clearedDungeonIds.Contains(dungeon.Id)
        }).ToList();
    }

    public async Task<DungeonSummaryResponse?> GetDungeonAsync(int dungeonId, string? token) =>
        (await GetDungeonsAsync(token)).SingleOrDefault(dungeon => dungeon.DungeonId == dungeonId);

    public Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(string monsterType, string? token) =>
        CreateRoomAsync(null, monsterType, token);

    public async Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(int? dungeonId, string? legacyMonsterType, string? token)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (await dbContext.RoomSlots.AnyAsync(x => x.CharacterId == character!.Id)) return (null, "CharacterAlreadyInRoom");

        await DbInitializer.EnsureDefaultDungeonsAsync(dbContext);
        var dungeon = dungeonId.HasValue
            ? await dbContext.Dungeons.FindAsync(dungeonId.Value)
            : await dbContext.Dungeons.FirstOrDefaultAsync(item => item.MonsterName == legacyMonsterType) ?? await dbContext.Dungeons.OrderBy(item => item.SortOrder).FirstAsync();
        if (dungeon is null) return (null, "DungeonNotFound");
        var monster = new Monster { Name = dungeon.MonsterName, Hp = dungeon.MonsterMaxHp, MaxHp = dungeon.MonsterMaxHp, Attack = dungeon.MonsterAttack, Defense = dungeon.MonsterDefense };
        var room = new Room { DungeonId = dungeon.Id, MonsterId = 0, OwnerUserId = user!.Id, SlotCount = dungeon.SlotCount, Status = RoomStatus.NotStarted };
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        dbContext.Monsters.Add(monster);
        await dbContext.SaveChangesAsync();
        room.MonsterId = monster.Id;
        dbContext.Rooms.Add(room);
        await dbContext.SaveChangesAsync();
        dbContext.RoomSlots.AddRange(Enumerable.Range(1, room.SlotCount).Select(index => new RoomSlot
        {
            RoomId = room.Id,
            SlotIndex = index,
            CharacterId = index == 1 ? character.Id : null,
            UserId = index == 1 ? user.Id : null,
            IsMainControl = index == 1
        }));
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> JoinRoomAsync(int roomId, JoinRoomRequest request, string? token)
    {
        var room = await dbContext.Rooms.FindAsync(roomId);
        if (room is null) return (null, "NotFound");
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (room.OwnerUserId == user!.Id) return (null, "CannotJoinOwnRoom");
        if (room.Status is RoomStatus.Preparing or RoomStatus.Cooldown) return (null, "RoomLocked");
        if (room.Status == RoomStatus.BattleOver) return (null, "BattleOver");
        if (request.SlotIndex < 1 || request.SlotIndex > room.SlotCount) return (null, "InvalidSlotIndex");
        if (await dbContext.RoomSlots.AnyAsync(x => x.RoomId == roomId && x.UserId == user.Id)) return (null, "AlreadyInRoom");
        if (await dbContext.RoomSlots.AnyAsync(x => x.CharacterId == character!.Id)) return (null, "CharacterAlreadyInRoom");
        var slot = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == roomId && x.SlotIndex == request.SlotIndex);
        if (slot.CharacterId.HasValue) return (null, "SlotOccupied");
        slot.CharacterId = character.Id;
        slot.UserId = user.Id;
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> LeaveRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FindAsync(roomId);
        if (room is null) return (null, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        if (room.OwnerUserId == user!.Id) return (null, "NotRoomParticipant");
        if (room.Status is RoomStatus.Preparing or RoomStatus.Cooldown) return (null, "RoomLocked");
        var slot = await dbContext.RoomSlots.SingleOrDefaultAsync(x => x.RoomId == roomId && x.UserId == user.Id);
        if (slot is null) return (null, "NotRoomParticipant");
        slot.CharacterId = null;
        slot.UserId = null;
        slot.IsConfirmed = false;
        slot.IsAutoEnabled = false;
        slot.IsTemporaryAuto = false;
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> AssignSlotAsync(int roomId, AssignRoomSlotRequest request, string? token)
    {
        var (room, user, error) = await GetOwnerEditableRoomAsync(roomId, token);
        if (error is not null) return (null, error);
        if (request.SlotIndex < 1 || request.SlotIndex > room!.SlotCount) return (null, "InvalidSlotIndex");
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == request.CharacterId);
        if (character is null) return (null, "CharacterNotFound");
        if (character.UserId != user!.Id) return (null, "NotCharacterOwner");
        var existingSlot = await dbContext.RoomSlots.FirstOrDefaultAsync(x => x.CharacterId == character.Id);
        if (existingSlot is not null && existingSlot.RoomId != room!.Id) return (null, "CharacterAlreadyInRoom");
        if (existingSlot is not null && existingSlot.SlotIndex != request.SlotIndex) return (null, "CharacterAlreadyInTargetRoom");
        var target = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == room!.Id && x.SlotIndex == request.SlotIndex);
        if (target.CharacterId.HasValue && target.CharacterId != character.Id) return (null, "SlotOccupied");
        if (target.IsMainControl && target.CharacterId != character.Id) return (null, "CannotReplaceMainControl");
        target.CharacterId = character.Id;
        target.UserId = user.Id;
        room!.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> RemoveSlotAsync(int roomId, int slotIndex, string? token)
    {
        var (room, user, error) = await GetOwnerEditableRoomAsync(roomId, token);
        if (error is not null) return (null, error);
        if (slotIndex < 1 || slotIndex > room!.SlotCount) return (null, "InvalidSlotIndex");
        var slot = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == room!.Id && x.SlotIndex == slotIndex);
        if (slot.IsMainControl) return (null, "CannotRemoveMainControl");
        if (slot.UserId != user!.Id) return (null, "NotCharacterOwner");
        slot.CharacterId = null;
        slot.UserId = null;
        slot.IsConfirmed = false;
        slot.IsAutoEnabled = false;
        slot.IsTemporaryAuto = false;
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user!.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> SetPreparationTimeoutAsync(int roomId, SetPreparationTimeoutRequest request, string? token)
    {
        var (room, user, error) = await GetOwnerEditableRoomAsync(roomId, token);
        if (error is not null) return (null, error);
        var isMixedTeam = await dbContext.RoomSlots.AnyAsync(slot => slot.RoomId == room!.Id && slot.UserId.HasValue && slot.UserId != room.OwnerUserId);
        if (isMixedTeam && !request.IsEnabled) return (null, "MixedTeamTimeoutRequired");
        room.IsSelfTeamPreparationTimeoutEnabled = request.IsEnabled;
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user!.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> SetMainControlAsync(int roomId, SetMainControlRequest request, string? token)
    {
        var (room, user, error) = await GetOwnerEditableRoomAsync(roomId, token);
        if (error is not null) return (null, error);
        var slot = await dbContext.RoomSlots.SingleOrDefaultAsync(x => x.RoomId == room!.Id && x.CharacterId == request.CharacterId && x.UserId == user!.Id);
        if (slot is null) return (null, "CharacterNotInRoom");
        var slots = await dbContext.RoomSlots.Where(x => x.RoomId == room.Id).ToListAsync();
        foreach (var item in slots) item.IsMainControl = item.Id == slot.Id;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(bool Success, string? Error)> DeleteRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (false, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (false, error);
        if (room.OwnerUserId != user!.Id) return (false, "NotOwner");
        dbContext.RoomSlots.RemoveRange(await dbContext.RoomSlots.Where(x => x.RoomId == roomId).ToListAsync());
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        dbContext.Rooms.Remove(room);
        if (monster is not null) dbContext.Monsters.Remove(monster);
        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    private async Task<(Room? Room, User? User, string? Error)> GetOwnerEditableRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, null, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (room, null, error);
        if (room.OwnerUserId != user!.Id) return (room, user, "NotOwner");
        if (room.Status is RoomStatus.Preparing or RoomStatus.Cooldown) return (room, user, "FormationLocked");
        return (room, user, null);
    }

    private async Task<RoomDetailResponse?> BuildRoomDetailAsync(Room room, int? currentUserId)
    {
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        if (monster is null || dungeon is null) return null;
        var slots = await dbContext.RoomSlots.Where(x => x.RoomId == room.Id).OrderBy(x => x.SlotIndex).ToListAsync();
        var characterIds = slots.Where(x => x.CharacterId.HasValue).Select(x => x.CharacterId!.Value).ToList();
        var characters = await dbContext.Characters.Where(x => characterIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var userIds = slots.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).ToList();
        var users = await dbContext.Users.Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var now = DateTime.UtcNow;
        var aliveSlots = slots.Where(x => x.CharacterId.HasValue && characters.TryGetValue(x.CharacterId.Value, out var character) && character.Hp > 0).ToList();
        var currentUserAliveSlots = aliveSlots.Where(x => x.UserId == currentUserId).ToList();
        var clearedDungeonUserIds = await dbContext.UserDungeonClears.Where(clear => clear.DungeonId == room.DungeonId).Select(clear => clear.UserId).ToListAsync();
        var isCurrentUserAutoUnlocked = currentUserId.HasValue && clearedDungeonUserIds.Contains(currentUserId.Value);
        var isMixedTeam = slots.Any(slot => slot.UserId.HasValue && slot.UserId != room.OwnerUserId);
        var isPreparationTimeoutEnabled = isMixedTeam || room.IsSelfTeamPreparationTimeoutEnabled;
        var isAllAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(slot => IsSlotAuto(room, slot, clearedDungeonUserIds));
        return new RoomDetailResponse
        {
            RoomId = room.Id, OwnerUserId = room.OwnerUserId, DungeonId = dungeon.Id, DungeonName = dungeon.Name, SlotCount = room.SlotCount,
            MonsterName = monster.Name, MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp,
            RoomStatus = room.Status, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, PreparationStartedAtUtc = room.PreparationStartedAtUtc, PreparationExpiresAtUtc = isPreparationTimeoutEnabled ? room.PreparationStartedAtUtc?.AddSeconds(30) : null, BattleEndedAtUtc = room.BattleEndedAtUtc,
            ServerTimeUtc = now,
            CanExecuteRound = room.Status == RoomStatus.Preparing && aliveSlots.Count > 0 && aliveSlots.All(x => x.IsConfirmed) && monster.Hp > 0,
            IsMixedTeam = isMixedTeam, IsPreparationTimeoutEnabled = isPreparationTimeoutEnabled, CanConfigurePreparationTimeout = currentUserId == room.OwnerUserId && !isMixedTeam, PreparationTimeoutSeconds = 30, IsCurrentUserAutoUnlocked = isCurrentUserAutoUnlocked, IsAllAliveMembersAuto = isAllAliveMembersAuto,
            CanPrepare = currentUserAliveSlots.Any(x => !x.IsConfirmed) && !isAllAliveMembersAuto && monster.Hp > 0 && room.Status != RoomStatus.BattleOver && (room.Status == RoomStatus.Preparing || !room.NextRoundAvailableAtUtc.HasValue || room.NextRoundAvailableAtUtc <= now),
            CanLeaveRoom = currentUserId.HasValue && currentUserId != room.OwnerUserId && room.Status is not (RoomStatus.Preparing or RoomStatus.Cooldown) && slots.Any(x => x.UserId == currentUserId),
            Slots = slots.Select(slot =>
            {
                characters.TryGetValue(slot.CharacterId ?? 0, out var character);
                users.TryGetValue(slot.UserId ?? 0, out var player);
                return new RoomSlotResponse { SlotIndex = slot.SlotIndex, CharacterId = slot.CharacterId, CharacterName = character?.Name, CharacterHp = character?.Hp, CharacterMaxHp = character?.MaxHp, IsOccupied = slot.CharacterId.HasValue, IsMainControl = slot.IsMainControl, IsCurrentUserCharacter = slot.UserId == currentUserId, IsAlive = character?.Hp > 0, IsConfirmed = slot.IsConfirmed, PlayerName = player?.UserName, IsAutoEnabled = IsSlotAuto(room, slot, clearedDungeonUserIds), IsTemporaryAuto = slot.IsTemporaryAuto, IsAutoUnlockedForCurrentUser = slot.UserId == currentUserId && isCurrentUserAutoUnlocked, CanConfigureAuto = slot.UserId == currentUserId && isCurrentUserAutoUnlocked && character?.Hp > 0 && (slot.UserId != room.OwnerUserId || slot.IsMainControl) };
            }).ToList()
        };
    }

    private static bool IsSlotAuto(Room room, RoomSlot slot, List<int> clearedDungeonUserIds) =>
        slot.UserId.HasValue && clearedDungeonUserIds.Contains(slot.UserId.Value) &&
        (slot.IsAutoEnabled || (slot.UserId == room.OwnerUserId && !slot.IsMainControl));

    private async Task<RoomSummaryResponse?> BuildRoomSummaryAsync(Room room)
    {
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        return monster is null ? null : new RoomSummaryResponse { RoomId = room.Id, MonsterName = monster.Name, MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp, RoomStatus = room.Status };
    }

}
