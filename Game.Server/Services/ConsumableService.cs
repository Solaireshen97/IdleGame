using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class ConsumableService(GameDbContext dbContext, UserService userService, ConsumableCatalog catalog)
{
    public async Task<(CharacterConsumablesResponse? Response, string? Error)> GetAsync(string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        return error is null ? (await BuildResponseAsync(character!), null) : (null, error);
    }

    public async Task<(CharacterConsumablesResponse? Response, string? Error)> SetSlotAsync(
        string? token, int characterId, int slotIndex, SetConsumableSlotRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (slotIndex < 1 || slotIndex > ConsumableRules.OperationPotionSlotIndex) return (null, "InvalidSlotIndex");
        if (request.AutoHpThresholdPercent is < 1 or > 100) return (null, "InvalidHpThreshold");

        var item = request.ItemCode is null ? null : catalog.FindItem(request.ItemCode.Trim());
        if (request.ItemCode is not null && item is null) return (null, "UnknownConsumable");
        if (item is not null && !ConsumableRules.CanEquip(slotIndex, item.Kind)) return (null, "WrongConsumableSlot");

        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        if (roomSlot is not null) return (null, "LoadoutLocked");

        var equipped = await dbContext.CharacterConsumableSlots
            .Where(slot => slot.CharacterId == characterId)
            .ToListAsync();
        if (item is not null && equipped.Any(slot => slot.SlotIndex != slotIndex &&
            string.Equals(slot.ItemCode, item.Code, StringComparison.OrdinalIgnoreCase)))
            return (null, "ConsumableAlreadyEquipped");

        var slotToUpdate = equipped.SingleOrDefault(slot => slot.SlotIndex == slotIndex);
        if (slotToUpdate is null)
        {
            slotToUpdate = new CharacterConsumableSlot { CharacterId = characterId, SlotIndex = slotIndex };
            dbContext.CharacterConsumableSlots.Add(slotToUpdate);
        }
        else
        {
            slotToUpdate.Version++;
        }
        slotToUpdate.ItemCode = item?.Code;
        slotToUpdate.AutoUseEnabled = item is { Kind: "Healing" or "CombatBuff" } && request.AutoUseEnabled;
        slotToUpdate.AutoHpThresholdPercent = request.AutoHpThresholdPercent;
        character!.Version++;

        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            return (null, "ConcurrencyConflict");
        }
    }

    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        return await new CharacterAccessResolver(dbContext).OwnedAsync(user!, characterId);
    }

    private async Task<CharacterConsumablesResponse> BuildResponseAsync(Character character)
    {
        var inventory = await dbContext.CharacterItemStacks
            .Where(stack => stack.CharacterId == character.Id)
            .ToDictionaryAsync(stack => stack.ItemCode, stack => stack.Quantity);
        var equipped = await dbContext.CharacterConsumableSlots
            .Where(slot => slot.CharacterId == character.Id)
            .ToDictionaryAsync(slot => slot.SlotIndex);

        return new CharacterConsumablesResponse
        {
            CharacterId = character.Id,
            CharacterName = character.Name,
            HealingPotionUsesLimit = ConsumableRules.HealingPotionUsesPerRun,
            Items = catalog.Items.OrderBy(item => item.Code).Select(item => new ConsumableItemResponse
            {
                Code = item.Code,
                Name = item.Name,
                Kind = item.Kind,
                HealAmount = item.Kind == "Healing" ? ConsumableCatalog.HealAmountFor(item, TalentRules.EffectiveMaxHp(character), character.Level) : 0,
                AttackPercent = item.AttackPercent,
                CooldownRounds = item.CooldownRounds,
                Tier = item.Tier,
                Description = ConsumableCatalog.Description(item, character.Level),
                WeaponSkillCode = item.WeaponSkillCode,
                Quantity = inventory.GetValueOrDefault(item.Code)
            }).ToList(),
            Slots = Enumerable.Range(1, ConsumableRules.OperationPotionSlotIndex).Select(index =>
            {
                equipped.TryGetValue(index, out var slot);
                return new ConsumableSlotResponse
                {
                    SlotIndex = index,
                    ItemCode = slot?.ItemCode,
                    AutoUseEnabled = slot?.AutoUseEnabled ?? false,
                    AutoHpThresholdPercent = slot?.AutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent
                };
            }).ToList()
        };
    }
}
