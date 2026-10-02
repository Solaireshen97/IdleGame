using System.Text.Json;
using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Formations;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public partial class RoomService
{
    private sealed record SelectedLoadout(CombatLoadoutDefinition Definition, int? Id, int? Version, string Name, bool Remember);

    private async Task<(User? User, Character? Character, string? Error)> GetAdmissionCharacterAsync(string? token, int? characterId)
    {
        if (!characterId.HasValue) return await userService.GetCurrentUserAndActiveCharacterAsync(token);
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, null, error);
        var (character, characterError) = await new CharacterAccessResolver(dbContext).OwnedAsync(user!, characterId.Value);
        return (user, character, characterError);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(int? dungeonId, string? legacyMonsterType,
        string? token, bool isRepeatBattle = false, bool isPreparationTimeoutEnabled = true, bool isPublic = false,
        int depthLevel = 1, int? characterId = null, LoadoutSelection? loadoutSelection = null, string? requestId = null)
    {
        var (user, character, error) = await GetAdmissionCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        return await WithLoadoutAdmissionAsync(user!, character!, "Create",
            new { dungeonId, legacyMonsterType, isRepeatBattle, isPreparationTimeoutEnabled, isPublic, depthLevel, loadoutSelection },
            loadoutSelection, requestId, () => CreateRoomWithoutLoadoutAsync(dungeonId, legacyMonsterType, user!, character!,
                isRepeatBattle, isPreparationTimeoutEnabled, isPublic, depthLevel));
    }

    private Task<(RoomDetailResponse? Detail, string? Error)> JoinRoomCoreAsync(int roomId, JoinRoomRequest request,
        User user, Character character, RoomOperation? frozen = null) => WithLoadoutAdmissionAsync(user, character, "Join",
            new { roomId, request.SlotIndex, request.LoadoutSelection }, request.LoadoutSelection, request.RequestId,
            () => JoinRoomWithoutLoadoutAsync(roomId, request, user, character), frozen);

    private async Task<(RoomDetailResponse? Detail, string? Error)> AssignSlotCoreAsync(int roomId, AssignRoomSlotRequest request,
        User user, RoomOperation? frozen = null)
    {
        var fingerprint = AdmissionFingerprint("Assign", request.CharacterId, new { roomId, request.SlotIndex, request.LoadoutSelection });
        if (await ReadAdmissionReceiptAsync(user.Id, "Assign", request.RequestId, fingerprint) is { } previous) return previous;
        var (_, roomError) = await GetOwnerEditableRoomAsync(roomId, user);
        if (roomError is not null) return (null, roomError);
        var character = await dbContext.Characters.FindAsync(request.CharacterId);
        if (character is null) return (null, "CharacterNotFound");
        if (character.UserId != user.Id) return (null, "NotCharacterOwner");
        var existing = await dbContext.RoomSlots.SingleOrDefaultAsync(s => s.CharacterId == character!.Id);
        if (existing is not null && existing.RoomId == roomId)
        {
            if (request.LoadoutSelection?.Mode == "SavedFormation" || frozen?.RequestedLoadoutJson is not null)
                return (null, "LoadoutLocked");
            return await AssignSlotWithoutLoadoutAsync(roomId, request, user);
        }
        return await WithLoadoutAdmissionAsync(user, character!, "Assign", new { roomId, request.SlotIndex, request.LoadoutSelection },
            request.LoadoutSelection, request.RequestId, () => AssignSlotWithoutLoadoutAsync(roomId, request, user), frozen);
    }

    private async Task<(SelectedLoadout? Selected, string? Error)> SelectLoadoutAsync(Character character,
        LoadoutSelection? selection, RoomOperation? frozen = null)
    {
        if (loadouts is null) return selection?.Mode == "SavedFormation" ? (null, "FormationUnavailable") : (null, null);
        if (frozen?.RequestedLoadoutJson is { } json)
        {
            try { return (new(CombatLoadoutCodec.Deserialize(json), frozen.SourceFormationId, frozen.SourceFormationVersion,
                frozen.SourceFormationName ?? "当前配置", frozen.RememberForEncounter), null); }
            catch (FormatException) { return (null, "UnsupportedLoadoutSnapshot"); }
        }
        if (selection is null || selection.Mode == "Current")
            return (new(await loadouts.CaptureAsync(character.Id), null, null, "当前配置", false), null);
        if (selection.Mode != "SavedFormation" || !selection.FormationId.HasValue || !selection.ExpectedVersion.HasValue)
            return (null, "InvalidLoadoutSelection");
        var formation = await dbContext.CharacterBattleFormations.Include(f => f.Weapons).Include(f => f.Skills).Include(f => f.Consumables)
            .SingleOrDefaultAsync(f => f.Id == selection.FormationId && f.CharacterId == character.Id && !f.IsDeleted);
        if (formation is null) return (null, "FormationNotFound");
        if (formation.Version != selection.ExpectedVersion) return (null, "FormationVersionConflict");
        return (new(CombatLoadoutService.FromFormation(formation), formation.Id, formation.Version, formation.Name, selection.RememberForEncounter), null);
    }

    private static string AdmissionFingerprint(string action, int characterId, object request) =>
        CombatLoadoutCodec.Hash(JsonSerializer.Serialize(new { action, CharacterId = characterId, request }));

    private async Task<(RoomDetailResponse? Detail, string? Error)?> ReadAdmissionReceiptAsync(int userId, string action, string? requestId, string fingerprint)
    {
        if (requestId is null) return null;
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100) return (null, "InvalidRequestId");
        var receipt = await dbContext.BattleAdmissionReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId && r.RequestId == requestId);
        if (receipt is null) return null;
        if (receipt.Action != action || receipt.Fingerprint != fingerprint) return (null, "RequestIdConflict");
        // The winning request can have changed an actor already loaded in this context.
        // Returning a receipt must read committed state, including the configuration used by integrity checks.
        dbContext.ChangeTracker.Clear();
        if (receipt.RoomId is { } roomId && await dbContext.Rooms.FindAsync(roomId) is { } room)
            return (await BuildRoomDetailAsync(room, userId), null);
        return (receipt.ResultJson is null ? null : JsonSerializer.Deserialize<RoomDetailResponse>(receipt.ResultJson), null);
    }

    private async Task<(RoomDetailResponse? Detail, string? Error)> WithLoadoutAdmissionAsync(User user, Character character,
        string action, object request, LoadoutSelection? selection, string? requestId,
        Func<Task<(RoomDetailResponse? Detail, string? Error)>> execute, RoomOperation? frozen = null)
    {
        if (requestId is not null && (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100)) return (null, "InvalidRequestId");
        var fingerprint = AdmissionFingerprint(action, character.Id, request);
        if (await ReadAdmissionReceiptAsync(user.Id, action, requestId, fingerprint) is { } previous) return previous;
        await using var owned = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync() : null;
        var transaction = owned ?? dbContext.Database.CurrentTransaction!;
        var savepoint = "admission_" + Guid.NewGuid().ToString("N");
        if (owned is null) await transaction.CreateSavepointAsync(savepoint);
        async Task RollbackAsync()
        {
            if (owned is null) await transaction.RollbackToSavepointAsync(savepoint); else await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
        }
        try
        {
            // Another identical request may finish while this request waits for the transaction.
            if (await ReadAdmissionReceiptAsync(user.Id, action, requestId, fingerprint) is { } committedResult)
            {
                if (owned is not null) await transaction.CommitAsync(); else await transaction.ReleaseSavepointAsync(savepoint);
                return committedResult;
            }
            if (await CharacterActivityManager.IsBusyAsync(dbContext, character.Id))
            { await RollbackAsync(); return (null, "CharacterAlreadyInRoom"); }
            var (chosen, error) = await SelectLoadoutAsync(character, selection, frozen);
            if (error is not null) { await RollbackAsync(); return (null, error); }
            if (chosen is not null)
            {
                var preview = await loadouts!.PreviewAsync(character, chosen.Definition);
                if (!preview.CanDeploy) { await RollbackAsync(); return (null, preview.Issues.First(i => i.Severity == "Error").Code); }
                // Current remains unchanged; accepted reservations have their own immutable choices.
                if (chosen.Id.HasValue || frozen?.RequestedLoadoutJson is not null)
                {
                    error = await loadouts.ApplyAsync(character, chosen.Definition);
                    if (error is not null) { await RollbackAsync(); return (null, error); }
                }
            }
            var result = await execute();
            if (result.Error is not null || result.Detail is null) { await RollbackAsync(); return result; }
            var admittedRoom = await dbContext.Rooms.FindAsync(result.Detail.RoomId);
            if (chosen is not null)
            {
                var slot = await dbContext.RoomSlots.SingleAsync(s => s.RoomId == result.Detail.RoomId && s.CharacterId == character.Id);
                slot.SourceFormationId = chosen.Id; slot.SourceFormationVersion = chosen.Version; slot.SourceFormationName = chosen.Name;
                slot.AppliedLoadoutJson = CombatLoadoutCodec.Serialize(await loadouts!.CaptureAsync(character.Id));
                slot.AutoPolicyOverridesJson = null;
                if (chosen.Id.HasValue)
                {
                    var state = await dbContext.CharacterFormationStates.FindAsync(character.Id);
                    if (state is null) { state = new() { CharacterId = character.Id }; dbContext.CharacterFormationStates.Add(state); }
                    state.AppliedFormationId = chosen.Id; state.AppliedFormationVersion = chosen.Version;
                    state.AppliedChoiceHash = CombatLoadoutCodec.ConfigurationHash(CombatLoadoutCodec.Deserialize(slot.AppliedLoadoutJson)); state.Version++;
                    if (chosen.Remember && await dbContext.CharacterBattleFormations.AnyAsync(f => f.Id == chosen.Id && !f.IsDeleted))
                    {
                        var dungeon = await dbContext.Dungeons.FindAsync(admittedRoom!.DungeonId);
                        var preference = await dbContext.CharacterBattleFormationPreferences.FindAsync(character.Id, dungeon!.Code, admittedRoom.DepthLevel);
                        if (preference is null)
                        {
                            preference = new() { CharacterId = character.Id, DungeonCode = dungeon.Code, DepthLevel = admittedRoom.DepthLevel };
                            dbContext.CharacterBattleFormationPreferences.Add(preference);
                        }
                        preference.FormationId = chosen.Id.Value; preference.LastUsedAtUtc = DateTime.UtcNow; preference.Version++;
                    }
                }
            }
            await dbContext.SaveChangesAsync();
            var detail = await BuildRoomDetailAsync(admittedRoom!, user.Id);
            if (requestId is not null)
            {
                dbContext.BattleAdmissionReceipts.Add(new() { UserId = user.Id, RequestId = requestId, Action = action,
                    Fingerprint = fingerprint, RoomId = admittedRoom!.Id, CharacterId = character.Id, ResultJson = JsonSerializer.Serialize(detail) });
                await dbContext.SaveChangesAsync();
            }
            if (owned is not null) await transaction.CommitAsync(); else await transaction.ReleaseSavepointAsync(savepoint);
            return (detail, null);
        }
        catch (Exception exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await RollbackAsync();
            // A concurrent identical request may have committed while this request was writing.
            if (owned is not null)
            {
                await owned.DisposeAsync();
                if (await ReadAdmissionReceiptAsync(user.Id, action, requestId, fingerprint) is { } previousResult) return previousResult;
            }
            return (null, "ConcurrencyConflict");
        }
        catch (UnsupportedCombatSkillLoadoutVersionException)
        { await RollbackAsync(); return (null, "UnsupportedSkillLoadoutVersion"); }
        catch
        { await RollbackAsync(); throw; }
    }

    private async Task<string?> CaptureOperationLoadoutAsync(RoomOperation operation, LoadoutSelection? selection)
    {
        if (operation.Kind is not (Game.Shared.Enums.RoomOperationKind.Join or Game.Shared.Enums.RoomOperationKind.Assign)) return null;
        if (operation.ExpectedSourceSlotIndex.HasValue)
            return selection?.Mode == "SavedFormation" ? "LoadoutLocked" : null;
        var character = await dbContext.Characters.FindAsync(operation.CharacterId);
        var (chosen, error) = await SelectLoadoutAsync(character!, selection);
        if (error is not null || chosen is null) return error;
        var preview = await loadouts!.PreviewAsync(character!, chosen.Definition);
        if (!preview.CanDeploy) return preview.Issues.First(i => i.Severity == "Error").Code;
        operation.SourceFormationId = chosen.Id; operation.SourceFormationVersion = chosen.Version; operation.SourceFormationName = chosen.Name;
        operation.RequestedLoadoutJson = CombatLoadoutCodec.Serialize(chosen.Definition);
        operation.RememberForEncounter = chosen.Remember;
        return null;
    }

    private static void ClearSlotLoadout(RoomSlot slot)
    {
        slot.SourceFormationId = null; slot.SourceFormationVersion = null; slot.SourceFormationName = null;
        slot.AppliedLoadoutJson = null; slot.AutoPolicyOverridesJson = null;
    }
}
