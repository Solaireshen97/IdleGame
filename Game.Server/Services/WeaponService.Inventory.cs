using System.Text.Json;
using Game.Server.Data;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed partial class WeaponService
{
    private async Task<(CharacterWeaponsResponse? Response, string? Error)> ExecuteInventoryBatchAsync(
        string? token, int characterId, WeaponBatchRequest request, string action)
    {
        var (user, authError) = await userService.GetCurrentUserEntityAsync(token);
        if (authError is not null) return (null, authError);
        if (!InventoryBatchRules.ValidSelection(request.WeaponIds)) return (null, "InvalidWeaponSelection");
        if (request.ExpectedVersions?.Any(v => v is null) == true) return (null, "InventoryPreviewRequired");
        var userId = user!.Id;
        // Standalone commands must not reuse entities read before acquiring the writer transaction.
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            var character = await dbContext.Characters.SingleOrDefaultAsync(c => c.Id == characterId);
            if (character is null) return (null, "CharacterNotFound");
            if (character.UserId != userId) return (null, "NotOwner");
            var validId = Guid.TryParse(request.RequestId, out var requestGuid) && requestGuid != Guid.Empty;
            var requestId = validId ? requestGuid.ToString("N") : "";
            var fingerprint = InventoryBatchRules.RequestFingerprint(InventoryKinds.Weapon, action,
                request.WeaponIds, request.ExpectedVersions, request.OutcomeFingerprint);
            var kind = action == "sell" ? "InventoryWeaponSell" : "InventoryWeaponDismantle";
            var prior = validId ? await dbContext.LogisticsRequests.AsNoTracking().SingleOrDefaultAsync(r => r.CharacterId == characterId && r.RequestId == requestId) : null;
            if (prior is not null)
            {
                if (prior.Kind != kind || prior.Fingerprint != fingerprint) return (null, "RequestIdReused");
                var replay = await BuildResponseAsync(character);
                replay.OperationResult = JsonSerializer.Deserialize<InventoryOperationResult>(prior.ResultJson!);
                return (replay, null);
            }
            var items = await LoadSelectedWeaponsAsync(characterId, request.WeaponIds);
            if (items.Count != request.WeaponIds.Count) return (null, "WeaponNotOwned");
            var references = await InventoryReferences.LoadAsync(dbContext, characterId);
            var inRoom = await dbContext.RoomSlots.AnyAsync(s => s.CharacterId == characterId);
            var preview = InventoryBatchRules.PreviewWeapons(characterId, action, items, weaponCatalog, inRoom, references);
            if (InventoryActionPolicy.Error(preview.Items, action) is { } protectedError) return (null, protectedError);
            if (InventoryBatchRules.ValidateConfirmation(request.RequestId, request.WeaponIds, request.ExpectedVersions,
                    request.OutcomeFingerprint, preview) is { } confirmationError) return (null, confirmationError);
            if (await InventoryBatchRules.CreditAsync(dbContext, character, preview.Rewards) is { } limitError) return (null, limitError);
            var result = InventoryBatchRules.Receipt(requestId, preview);
            dbContext.CharacterWeapons.RemoveRange(items);
            dbContext.LogisticsRequests.Add(new LogisticsRequest { CharacterId = characterId, RequestId = requestId,
                Kind = kind, Fingerprint = fingerprint, ResultJson = JsonSerializer.Serialize(result), CompletedAtUtc = DateTime.UtcNow });
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            var response = await BuildResponseAsync(character);
            response.OperationResult = result;
            return (response, null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync(); dbContext.ChangeTracker.Clear(); return (null, "ConcurrencyConflict");
        }
    }
}
