using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class SoulImprintService(GameDbContext dbContext, UserService userService,
    SoulImprintCatalog catalog)
{
    public async Task<(CharacterSoulImprintsResponse? Response, string? Error)> GetAsync(
        string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        return error is null ? (await BuildResponseAsync(character!), null) : (null, error);
    }

    public async Task<(CharacterSoulImprintsResponse? Response, string? Error)> SetEquippedAsync(
        string? token, int characterId, SetSoulImprintRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (request.SoulImprintId is <= 0) return (null, "SoulImprintNotOwned");
        if (await GetLoadoutLockErrorAsync(characterId) is { } lockError) return (null, lockError);

        var imprints = await dbContext.CharacterSoulImprints
            .Where(item => item.CharacterId == characterId).ToListAsync();
        var selected = request.SoulImprintId.HasValue
            ? imprints.SingleOrDefault(item => item.Id == request.SoulImprintId.Value)
            : null;
        if (request.SoulImprintId.HasValue && selected is null) return (null, "SoulImprintNotOwned");
        var equipped = imprints.SingleOrDefault(item => item.EquippedSlotIndex == 1);
        if (equipped?.Id == selected?.Id) return (await BuildResponseAsync(character!), null);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            if (equipped is not null)
            {
                equipped.EquippedSlotIndex = null;
                equipped.Version++;
                // SQLite unique filtered indexes validate each statement. Persist the removal before
                // assigning the single equipment slot to another instance.
                await dbContext.SaveChangesAsync();
            }
            if (selected is not null)
            {
                selected.EquippedSlotIndex = 1;
                selected.Version++;
            }
            character!.Version++;
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterSoulImprintsResponse? Response, string? Error)> SetLockAsync(
        string? token, int characterId, int soulImprintId, SetSoulImprintLockRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var imprint = await dbContext.CharacterSoulImprints.SingleOrDefaultAsync(item =>
            item.Id == soulImprintId && item.CharacterId == characterId);
        if (imprint is null) return (null, "SoulImprintNotOwned");
        if (imprint.IsLocked == request.IsLocked) return (await BuildResponseAsync(character!), null);
        imprint.IsLocked = request.IsLocked;
        imprint.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterSoulImprintsResponse? Response, string? Error)> SetAutoAsync(
        string? token, int characterId, int soulImprintId, SetSoulImprintAutoRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var imprint = await dbContext.CharacterSoulImprints.SingleOrDefaultAsync(item =>
            item.Id == soulImprintId && item.CharacterId == characterId);
        if (imprint is null) return (null, "SoulImprintNotOwned");
        if (imprint.AutoUseEnabled == request.AutoUseEnabled) return (await BuildResponseAsync(character!), null);
        imprint.AutoUseEnabled = request.AutoUseEnabled;
        imprint.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterSoulImprintsResponse? Response, string? Error)> DismantleAsync(
        string? token, int characterId, SoulImprintBatchRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var ids = request.SoulImprintIds.Distinct().ToList();
        if (ids.Count is < 1 or > 100 || ids.Count != request.SoulImprintIds.Count || ids.Any(id => id <= 0))
            return (null, "InvalidSoulImprintSelection");
        if (await GetLoadoutLockErrorAsync(characterId) is { } lockError) return (null, lockError);
        var imprints = await dbContext.CharacterSoulImprints.Where(item =>
            item.CharacterId == characterId && ids.Contains(item.Id)).ToListAsync();
        if (imprints.Count != ids.Count) return (null, "SoulImprintNotOwned");
        if (imprints.Any(item => item.EquippedSlotIndex.HasValue)) return (null, "SoulImprintEquipped");
        if (imprints.Any(item => item.IsLocked)) return (null, "SoulImprintLocked");

        var returns = imprints.Select(item => catalog.Find(item.SoulImprintCode)!)
            .GroupBy(item => item.Tier)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.DismantleFragments));
        var fragmentCodes = returns.Keys.Select(WeaponRules.FragmentCode).ToList();
        var stacks = await dbContext.CharacterItemStacks.Where(stack =>
            stack.CharacterId == characterId && fragmentCodes.Contains(stack.ItemCode)).ToListAsync();

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            foreach (var (tier, quantity) in returns)
            {
                var code = WeaponRules.FragmentCode(tier);
                var stack = stacks.SingleOrDefault(item => item.ItemCode == code);
                if (stack is null)
                {
                    dbContext.CharacterItemStacks.Add(new CharacterItemStack
                    {
                        CharacterId = characterId, ItemCode = code, Quantity = quantity
                    });
                }
                else
                {
                    stack.Quantity = checked(stack.Quantity + quantity);
                    stack.Version++;
                }
            }
            character!.Version++;
            dbContext.CharacterSoulImprints.RemoveRange(imprints);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var character = await dbContext.Characters.SingleOrDefaultAsync(item => item.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user!.Id ? (character, null) : (null, "NotOwner");
    }

    private async Task<string?> GetLoadoutLockErrorAsync(int characterId)
    {
        var room = await (from slot in dbContext.RoomSlots
            join candidate in dbContext.Rooms on slot.RoomId equals candidate.Id
            where slot.CharacterId == characterId
            select candidate).SingleOrDefaultAsync();
        return room is not null && room.Status != RoomStatus.BattleOver &&
               (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0)
            ? "LoadoutLocked" : null;
    }

    private async Task<CharacterSoulImprintsResponse> BuildResponseAsync(Character character)
    {
        var imprints = await dbContext.CharacterSoulImprints.Where(item => item.CharacterId == character.Id)
            .OrderBy(item => item.EquippedSlotIndex == null).ThenBy(item => item.Id).ToListAsync();
        var fragmentStacks = await dbContext.CharacterItemStacks.Where(stack =>
            stack.CharacterId == character.Id && stack.ItemCode.StartsWith("weapon-fragment-t")).ToListAsync();
        var maximumTier = Math.Max(1, catalog.Items.Select(item => item.Tier).DefaultIfEmpty(1).Max());
        return new CharacterSoulImprintsResponse
        {
            CharacterId = character.Id,
            CharacterName = character.Name,
            Fragments = Enumerable.Range(1, maximumTier).Select(tier => new WeaponFragmentResponse
            {
                Tier = tier,
                Code = WeaponRules.FragmentCode(tier),
                Name = WeaponRules.FragmentName(tier),
                Quantity = fragmentStacks.SingleOrDefault(stack => stack.ItemCode == WeaponRules.FragmentCode(tier))?.Quantity ?? 0
            }).ToList(),
            SoulImprints = imprints.Select(item =>
            {
                var definition = catalog.Find(item.SoulImprintCode)!;
                return new CharacterSoulImprintResponse
                {
                    Id = item.Id, Code = definition.Code, Name = definition.Name,
                    Description = definition.Description, Tier = definition.Tier, Element = definition.Element,
                    EffectType = definition.EffectType, PowerPercent = definition.PowerPercent,
                    SecondaryPowerPercent = definition.SecondaryPowerPercent,
                    DurationRounds = definition.DurationRounds,
                    InitialCooldownRounds = definition.InitialCooldownRounds,
                    CooldownRounds = definition.CooldownRounds,
                    DismantleFragments = definition.DismantleFragments,
                    IsEquipped = item.EquippedSlotIndex == 1, AutoUseEnabled = item.AutoUseEnabled,
                    IsLocked = item.IsLocked
                };
            }).ToList()
        };
    }
}
