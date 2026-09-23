using Game.Server.Data;
using Game.Shared.Dtos.Warehouse;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class WarehouseService(GameDbContext db, UserService userService,
    ConsumableCatalog consumables, MaterialCatalog materials)
{
    public async Task<(WarehouseResponse? Response, string? Error)> GetAsync(string? token)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        return error is null ? (await BuildResponseAsync(user!, character!), null) : (null, error);
    }

    public async Task<(WarehouseResponse? Response, string? Error)> TransferAsync(
        string? token, WarehouseTransferRequest request)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        if (request.RequestId == Guid.Empty) return (null, "InvalidRequestId");
        if (request.Quantity <= 0) return (null, "InvalidQuantity");
        if (request.Direction is not ("Deposit" or "Withdraw")) return (null, "InvalidDirection");

        var code = request.ItemCode?.Trim();
        var consumable = consumables.FindItem(code);
        var material = materials.FindItem(code);
        if (consumable is null && material is null) return (null, "ItemNotFound");
        if (consumable?.CanStoreInWarehouse != true && material?.CanStoreInWarehouse != true)
            return (null, "ItemBound");
        code = consumable?.Code ?? material!.Code;
        var requestId = request.RequestId.ToString("N");
        var existing = await db.WarehouseTransferRecords.AsNoTracking().SingleOrDefaultAsync(record =>
            record.UserId == user!.Id && record.RequestId == requestId);
        if (existing is not null)
            return IsSameTransfer(existing, character.Id, code, request)
                ? (await BuildResponseAsync(user!, character), null)
                : (null, "RequestIdReused");

        var bag = await db.CharacterItemStacks.SingleOrDefaultAsync(stack =>
            stack.CharacterId == character.Id && stack.ItemCode == code);
        var warehouse = await db.UserWarehouseStacks.SingleOrDefaultAsync(stack =>
            stack.UserId == user!.Id && stack.ItemCode == code);
        if (request.Direction == "Deposit")
        {
            if (bag is null || bag.Quantity < request.Quantity) return (null, "InsufficientCharacterItems");
            if (warehouse is not null && warehouse.Quantity > int.MaxValue - request.Quantity)
                return (null, "InventoryLimitReached");
            bag.Quantity -= request.Quantity;
            bag.Version++;
            if (warehouse is null)
            {
                db.UserWarehouseStacks.Add(new UserWarehouseStack
                {
                    UserId = user!.Id, ItemCode = code, Quantity = request.Quantity
                });
            }
            else
            {
                warehouse.Quantity += request.Quantity;
                warehouse.Version++;
            }
        }
        else
        {
            if (warehouse is null || warehouse.Quantity < request.Quantity) return (null, "InsufficientWarehouseItems");
            if (bag is not null && bag.Quantity > int.MaxValue - request.Quantity)
                return (null, "InventoryLimitReached");
            warehouse.Quantity -= request.Quantity;
            warehouse.Version++;
            if (bag is null)
            {
                db.CharacterItemStacks.Add(new CharacterItemStack
                {
                    CharacterId = character.Id, ItemCode = code, Quantity = request.Quantity
                });
            }
            else
            {
                bag.Quantity += request.Quantity;
                bag.Version++;
            }
        }

        user!.Version++;
        character.Version++;
        db.WarehouseTransferRecords.Add(new WarehouseTransferRecord
        {
            UserId = user!.Id, RequestId = requestId, CharacterId = character.Id,
            ItemCode = code, Direction = request.Direction, Quantity = request.Quantity,
            CreatedAtUtc = DateTime.UtcNow
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var recorded = await db.WarehouseTransferRecords.AsNoTracking().SingleOrDefaultAsync(record =>
                record.UserId == user.Id && record.RequestId == requestId);
            if (recorded is not null)
                return IsSameTransfer(recorded, character.Id, code, request)
                    ? (await BuildResponseAsync(user, character), null)
                    : (null, "RequestIdReused");
            return (null, "ConcurrencyConflict");
        }
        return (await BuildResponseAsync(user, character), null);
    }

    private static bool IsSameTransfer(WarehouseTransferRecord recorded, int characterId,
        string code, WarehouseTransferRequest request) =>
        recorded.CharacterId == characterId && recorded.ItemCode == code &&
        recorded.Direction == request.Direction && recorded.Quantity == request.Quantity;

    private async Task<WarehouseResponse> BuildResponseAsync(User user, Character character)
    {
        var bag = await db.CharacterItemStacks.AsNoTracking()
            .Where(stack => stack.CharacterId == character.Id)
            .ToDictionaryAsync(stack => stack.ItemCode, stack => stack.Quantity);
        var warehouse = await db.UserWarehouseStacks.AsNoTracking()
            .Where(stack => stack.UserId == user.Id)
            .ToDictionaryAsync(stack => stack.ItemCode, stack => stack.Quantity);
        var items = consumables.Items.Where(item => item.CanStoreInWarehouse).Select(item => new WarehouseItemResponse
        {
            Code = item.Code, Name = item.Name, Kind = "Consumable",
            Description = "战斗消耗品", CanTransfer = true,
            CharacterQuantity = bag.GetValueOrDefault(item.Code),
            WarehouseQuantity = warehouse.GetValueOrDefault(item.Code)
        }).Concat(materials.Items.Where(item => item.CanStoreInWarehouse).Select(item => new WarehouseItemResponse
        {
            Code = item.Code, Name = item.Name, Kind = "Material",
            Description = item.Description, CanTransfer = true,
            CharacterQuantity = bag.GetValueOrDefault(item.Code),
            WarehouseQuantity = warehouse.GetValueOrDefault(item.Code)
        })).OrderBy(item => item.Kind).ThenBy(item => item.Name).ToList();
        return new WarehouseResponse
        {
            CharacterId = character.Id, CharacterName = character.Name, Items = items
        };
    }
}
