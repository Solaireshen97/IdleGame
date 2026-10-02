using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public partial class RoomService
{
    public async Task<(RoomDetailResponse? Detail, string? Error)> SubmitOperationAsync(
        int roomId, SubmitRoomOperationRequest request, string? token)
    {
        var (user, authError) = await userService.GetCurrentUserEntityAsync(token);
        if (authError is not null) return (null, authError);
        if (request.RequestId is not null && (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 100))
            return (null, "InvalidRequestId");
        var fingerprint = CombatLoadoutCodec.Hash(System.Text.Json.JsonSerializer.Serialize(new
            { roomId, request.Kind, request.SlotIndex, request.CharacterId, request.LoadoutSelection }));
        if (request.RequestId is not null)
        {
            var previous = await dbContext.RoomOperations.AsNoTracking().SingleOrDefaultAsync(o => o.UserId == user!.Id && o.RequestId == request.RequestId);
            if (previous is not null)
            {
                if (previous.RequestFingerprint != fingerprint) return (null, "RequestIdConflict");
                dbContext.ChangeTracker.Clear();
                var previousRoom = await dbContext.Rooms.FindAsync(previous.RoomId);
                return previousRoom is null ? (null, "NotFound") : (await BuildRoomDetailAsync(previousRoom, user!.Id), null);
            }
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            var room = await dbContext.Rooms.FindAsync(roomId);
            if (room is null) return (null, "NotFound");
            var joiningCharacterId = request.Kind == RoomOperationKind.Join
                ? request.CharacterId ?? await new CharacterAccessResolver(dbContext).ActiveIdAsync(user!) : null;
            var operation = new RoomOperation
            {
                RoomId = roomId, UserId = user!.Id, Kind = request.Kind,
                SlotIndex = request.SlotIndex, CharacterId = request.Kind == RoomOperationKind.Assign
                    ? request.CharacterId ?? 0 : joiningCharacterId ?? 0,
                CreatedAtUtc = DateTime.UtcNow, RequestId = request.RequestId, RequestFingerprint = fingerprint
            };
            var error = await ValidateOperationAsync(room, operation, capture: true);
            if (error is not null) return (null, error);
            error = await CaptureOperationLoadoutAsync(operation, request.LoadoutSelection);
            if (error is not null) return (null, error);
            var pending = await dbContext.RoomOperations.Where(item => item.RoomId == roomId &&
                item.UserId == user.Id && item.Status == "Pending").ToListAsync();
            var duplicate = pending.FirstOrDefault(item => item.Kind == operation.Kind &&
                (request.RequestId == null || item.RequestId == request.RequestId) &&
                item.SlotIndex == operation.SlotIndex && item.CharacterId == operation.CharacterId &&
                item.ExpectedTargetCharacterId == operation.ExpectedTargetCharacterId &&
                item.ExpectedSourceSlotIndex == operation.ExpectedSourceSlotIndex &&
                item.RequestedLoadoutJson == operation.RequestedLoadoutJson &&
                item.SourceFormationId == operation.SourceFormationId &&
                item.SourceFormationVersion == operation.SourceFormationVersion &&
                item.RememberForEncounter == operation.RememberForEncounter);
            if (duplicate is not null) return (await BuildRoomDetailAsync(room, user.Id), null);

            // Independent changes can coexist; a newer conflicting choice replaces the old choice.
            foreach (var old in pending.Where(item => ConflictsWith(item, operation)))
                FinishOperation(old, "Cancelled", "Replaced");
            dbContext.RoomOperations.Add(operation);
            room.Version++;
            await dbContext.SaveChangesAsync();
            if (CanExecuteOperation(room, operation.Kind))
            {
                error = await ExecuteOperationAsync(operation, user);
                if (error is not null)
                {
                    await transaction.RollbackAsync();
                    dbContext.ChangeTracker.Clear();
                    return (null, error);
                }
                FinishOperation(operation, "Completed");
                await dbContext.SaveChangesAsync();
            }
            await transaction.CommitAsync();
            return (await BuildRoomDetailAsync(room, user.Id), null);
        }
        catch (Exception exception) when (IsOperationConflict(exception))
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            await transaction.DisposeAsync();
            if (request.RequestId is not null)
            {
                var previous = await dbContext.RoomOperations.AsNoTracking().SingleOrDefaultAsync(o => o.UserId == user!.Id && o.RequestId == request.RequestId);
                if (previous is not null)
                {
                    if (previous.RequestFingerprint != fingerprint) return (null, "RequestIdConflict");
                    var previousRoom = await dbContext.Rooms.FindAsync(previous.RoomId);
                    return previousRoom is null ? (null, "NotFound") : (await BuildRoomDetailAsync(previousRoom, user!.Id), null);
                }
            }
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(List<RoomOperationResponse>? Operations, string? Error)> GetOperationsAsync(int roomId, string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        return error is not null ? (null, error) : (await GetOperationResponsesAsync(roomId, user!.Id), null);
    }

    public async Task<(bool Success, string? Error)> CancelOperationAsync(int roomId, int operationId, string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (false, error);
        var operation = await dbContext.RoomOperations.SingleOrDefaultAsync(item => item.Id == operationId &&
            item.RoomId == roomId && item.UserId == user!.Id);
        if (operation is null) return (false, "NotFound");
        if (operation.Status != "Pending") return (false, "OperationAlreadyFinished");
        FinishOperation(operation, "Cancelled");
        try { await dbContext.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return (false, "ConcurrencyConflict"); }
        return (true, null);
    }

    public async Task ProcessPendingOperationsAsync(int roomId)
    {
        var ids = await dbContext.RoomOperations.AsNoTracking().Where(item => item.RoomId == roomId && item.Status == "Pending")
            .OrderBy(item => item.Id).Select(item => item.Id).ToListAsync();
        foreach (var id in ids)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            try
            {
                // Reload because a round, another worker, or a cancellation may have changed these rows.
                var operation = await dbContext.RoomOperations.FindAsync(id);
                if (operation is null) continue;
                await dbContext.Entry(operation).ReloadAsync();
                if (operation.Status != "Pending") continue;
                var room = await dbContext.Rooms.FindAsync(roomId);
                if (room is null) continue;
                await dbContext.Entry(room).ReloadAsync();
                var error = await ValidateOperationAsync(room, operation, capture: false);
                if (error is null && !CanExecuteOperation(room, operation.Kind)) continue;

                // Claim the same room version used by round settlement, in this transaction.
                room.Version++;
                operation.Version++;
                await dbContext.SaveChangesAsync();
                if (error is null)
                {
                    var user = await dbContext.Users.FindAsync(operation.UserId);
                    error = user is null ? "UserNotFound" : await ExecuteOperationAsync(operation, user);
                    if (error is "ConcurrencyConflict" or "CharacterAlreadyInRoom")
                    {
                        // A failed save can leave unrelated tracked mutations; roll back before retrying.
                        await transaction.RollbackAsync();
                        dbContext.ChangeTracker.Clear();
                        if (error == "ConcurrencyConflict") return;
                        continue;
                    }
                }
                // Admission rollback clears tracked entities; reload the claimed request before finishing it.
                operation = await dbContext.RoomOperations.FindAsync(id) ?? operation;
                FinishOperation(operation, error is null ? "Completed" : "Failed", error);
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception exception) when (IsOperationConflict(exception))
            {
                await transaction.RollbackAsync();
                dbContext.ChangeTracker.Clear();
                return; // Keep the request pending for the next scan.
            }
        }
    }

    private async Task<string?> ValidateOperationAsync(Room room, RoomOperation operation, bool capture)
    {
        if (!Enum.IsDefined(operation.Kind)) return "InvalidOperation";
        if (room.ClosedAtUtc.HasValue || room.IsRepeatBattle && room.ExpiresAtUtc <= DateTime.UtcNow) return "RoomClosed";
        var slots = await dbContext.RoomSlots.Where(slot => slot.RoomId == room.Id).ToListAsync();
        if (operation.Kind is RoomOperationKind.Leave or RoomOperationKind.Remove)
        {
            if (operation.Kind == RoomOperationKind.Leave && room.OwnerUserId == operation.UserId) return "NotRoomParticipant";
            if (operation.Kind == RoomOperationKind.Remove && room.OwnerUserId != operation.UserId) return "NotOwner";
            var slot = operation.Kind == RoomOperationKind.Leave
                ? slots.FirstOrDefault(item => item.UserId == operation.UserId && item.CharacterId.HasValue)
                : slots.FirstOrDefault(item => item.SlotIndex == operation.SlotIndex);
            if (slot is null || !slot.CharacterId.HasValue) return "NotRoomParticipant";
            if (slot.UserId != operation.UserId) return "NotCharacterOwner";
            if (!capture && slot.CharacterId != operation.CharacterId) return "FormationChanged";
            operation.SlotIndex = slot.SlotIndex;
            operation.CharacterId = slot.CharacterId.Value;
            operation.CharacterName = (await dbContext.Characters.FindAsync(operation.CharacterId))?.Name ?? "角色";
            return null;
        }

        if (operation.SlotIndex < 1 || operation.SlotIndex > room.SlotCount) return "InvalidSlotIndex";
        var target = slots.Single(item => item.SlotIndex == operation.SlotIndex);
        var character = await dbContext.Characters.FindAsync(operation.CharacterId);
        if (character is null) return "CharacterNotFound";
        if (character.UserId != operation.UserId) return "NotCharacterOwner";
        operation.CharacterName = character.Name;
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        if (dungeon is null) return "DungeonNotFound";
        var depthError = await _depthProgress.AdmissionErrorAsync(operation.UserId, dungeon, room.DepthLevel);
        var storyError = await new CampaignAccessService(dbContext, _depthCatalog).AdmissionErrorAsync(operation.UserId, dungeon);
        if (storyError is not null) return storyError;
        if (depthError is not null) return depthError;
        if (operation.Kind == RoomOperationKind.Join)
        {
            if (room.OwnerUserId == operation.UserId) return "CannotJoinOwnRoom";
            if (!room.IsPublic) return "RoomPrivate";
            if (slots.Any(slot => slot.UserId == operation.UserId)) return "AlreadyInRoom";
            if (room.Status == RoomStatus.BattleOver && (!room.IsRepeatBattle ||
                !await dbContext.Monsters.AnyAsync(monster => monster.Id == room.MonsterId && monster.Hp <= 0))) return "BattleOver";
            if (target.CharacterId.HasValue) return "SlotOccupied";
            if (await CharacterActivityManager.IsBusyAsync(dbContext, character.Id)) return "CharacterAlreadyInRoom";
        }
        else
        {
            if (room.OwnerUserId != operation.UserId) return "NotOwner";
            if (target.CharacterId.HasValue && target.UserId != operation.UserId) return "NotCharacterOwner";
            var source = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == character.Id);
            if (source is not null && source.RoomId != room.Id || source is null &&
                await dbContext.CharacterActivities.AnyAsync(activity => activity.CharacterId == character.Id)) return "CharacterAlreadyInRoom";
            if (target.CharacterId.HasValue && source is null) return "SlotOccupied";
            if (!capture && (target.CharacterId != operation.ExpectedTargetCharacterId || source?.SlotIndex != operation.ExpectedSourceSlotIndex))
                return "FormationChanged";
            if (capture)
            {
                operation.ExpectedTargetCharacterId = target.CharacterId;
                operation.ExpectedSourceSlotIndex = source?.SlotIndex;
            }
        }
        return null;
    }

    private static bool CanExecuteOperation(Room room, RoomOperationKind kind) => kind == RoomOperationKind.Join
        ? room.Status is RoomStatus.NotStarted or RoomStatus.Preparing or RoomStatus.Cooldown or RoomStatus.WaveTransition or RoomStatus.BattleOver
        : room.Status == RoomStatus.BattleOver || room.Status == RoomStatus.NotStarted && room.RoundNumber == 0;

    private async Task<string?> ExecuteOperationAsync(RoomOperation operation, User user)
    {
        var result = operation.Kind switch
        {
            RoomOperationKind.Join => await JoinRoomCoreAsync(operation.RoomId, new JoinRoomRequest { SlotIndex = operation.SlotIndex },
                user, (await dbContext.Characters.FindAsync(operation.CharacterId))!, operation),
            RoomOperationKind.Leave => await LeaveRoomCoreAsync(operation.RoomId, user),
            RoomOperationKind.Assign => await AssignSlotCoreAsync(operation.RoomId,
                new AssignRoomSlotRequest { SlotIndex = operation.SlotIndex, CharacterId = operation.CharacterId }, user, operation),
            _ => await RemoveSlotCoreAsync(operation.RoomId, operation.SlotIndex, user)
        };
        return result.Error;
    }

    private static bool ConflictsWith(RoomOperation old, RoomOperation next) =>
        old.Kind == RoomOperationKind.Leave || next.Kind == RoomOperationKind.Leave ||
        old.SlotIndex == next.SlotIndex || old.CharacterId == next.CharacterId ||
        old.ExpectedSourceSlotIndex == next.SlotIndex || next.ExpectedSourceSlotIndex == old.SlotIndex ||
        old.ExpectedSourceSlotIndex.HasValue && old.ExpectedSourceSlotIndex == next.ExpectedSourceSlotIndex;

    private static void FinishOperation(RoomOperation operation, string status, string? error = null)
    {
        operation.Status = status;
        operation.Error = error;
        operation.FinishedAtUtc = DateTime.UtcNow;
        operation.Version++;
    }

    private static bool IsOperationConflict(Exception exception) => DatabaseWriteErrors.IsConflict(exception);

    private async Task<List<RoomOperationResponse>> GetOperationResponsesAsync(int roomId, int? userId)
    {
        if (!userId.HasValue) return [];
        var operations = await dbContext.RoomOperations.AsNoTracking().Where(item => item.RoomId == roomId && item.UserId == userId)
            .OrderByDescending(item => item.Id).Take(30).ToListAsync();
        return operations.Select(item => new RoomOperationResponse
        {
            Id = item.Id, Kind = item.Kind, SlotIndex = item.SlotIndex, CharacterName = item.CharacterName,
            SourceFormationName = item.SourceFormationName, SourceFormationVersion = item.SourceFormationVersion,
            Status = item.Status, Error = item.Error, Message = GetOperationMessage(item)
        }).ToList();
    }

    private static string GetOperationMessage(RoomOperation operation)
    {
        var action = operation.Kind switch
        {
            RoomOperationKind.Join => $"{operation.CharacterName}加入 {operation.SlotIndex} 号位",
            RoomOperationKind.Leave => "退出房间",
            RoomOperationKind.Assign => $"将{operation.CharacterName}放到 {operation.SlotIndex} 号位",
            _ => $"移除 {operation.SlotIndex} 号位的{operation.CharacterName}"
        };
        return operation.Status switch
        {
            "Pending" => $"已预约{action}，{(operation.Kind == RoomOperationKind.Join ? "允许加入时" : "本场战斗结束后")}自动执行。",
            "Completed" => $"已完成：{action}。",
            "Cancelled" => operation.Error == "Replaced" ? $"已替换原预约：{action}。" : $"已取消：{action}。",
            _ => $"未能完成{action}：{GetOperationFailure(operation.Error)}"
        };
    }

    private static string GetOperationFailure(string? error) => error switch
    {
        "FormationChanged" or "SlotOccupied" => "队伍位置已变化，请重新选择。",
        "RoomClosed" => "房间已关闭或任务已到时限。",
        "RoomPrivate" => "房主已关闭加入。",
        "CharacterAlreadyInRoom" => "角色正在其他房间或任务中。",
        "DungeonDepthLocked" => "账号尚未开放这个深层等级。",
        "StoryMapLocked" => "账号尚未完成此地图的前置剧情。",
        "InvalidDungeonDepth" => "这个深层等级暂未开放。",
        "NotOwner" or "NotCharacterOwner" => "已无权执行此操作。",
        "NotRoomParticipant" or "AlreadyInRoom" => "角色的组队状态已变化。",
        "BattleOver" => "该房间已结束战斗，无法加入。",
        "MainWeaponRequired" or "WeaponNotOwned" => "预约编队的武器已不可用，请重新配置。",
        "SkillNotLearned" => "预约编队的技能已不可用，请重新配置。",
        "SoulImprintNotOwned" or "SoulImprintUnavailable" => "预约编队的魂印已不可用。",
        "UnsupportedLoadoutSnapshot" => "预约配装数据无法读取，请重新预约。",
        _ => "角色或房间状态已变化，请重新操作。"
    };
}
