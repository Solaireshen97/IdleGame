using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed partial class SoulImprintService(GameDbContext dbContext, UserService userService,
    SoulImprintCatalog catalog)
{
    public async Task<(CharacterSoulImprintsResponse? Response, string? Error)> GetAsync(
        string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is null && await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId) is { } roomSlot &&
            BattleAutoPolicyResolver.ValidationError(roomSlot) is { } snapshotError) return (null, snapshotError);
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
        if (selected is not null && catalog.Find(selected.SoulImprintCode) is null) return (null, "UnknownSoulImprint");
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
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
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
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterSoulImprintsResponse? Response, string? Error)> SetAutoAsync(
        string? token, int characterId, int soulImprintId, SetSoulImprintAutoRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var condition = SkillAutoRules.Normalize(request.AutoConditionOverride);
        if (!SkillAutoRules.IsValidOverride(condition)) return (null, "InvalidAutoCondition");
        if (request.AutoHpThresholdPercent is < 1 or > 100) return (null, "InvalidHpThreshold");
        var imprint = await dbContext.CharacterSoulImprints.SingleOrDefaultAsync(item =>
            item.Id == soulImprintId && item.CharacterId == characterId);
        if (imprint is null) return (null, "SoulImprintNotOwned");
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        if (roomSlot is not null && BattleAutoPolicyResolver.ValidationError(roomSlot) is { } policyError)
            return (null, policyError);
        var current = roomSlot is null
            ? new BattleAutoPolicyResolver.SkillPolicy(imprint.AutoUseEnabled, imprint.AutoConditionOverride, imprint.AutoHpThresholdPercent)
            : BattleAutoPolicyResolver.Soul(roomSlot, imprint);
        var threshold = request.AutoHpThresholdPercent ?? current.AutoHpThresholdPercent;
        if (request.AutoHpThresholdPercent is null && request.AutoConditionOverride is null)
            condition = current.AutoConditionOverride;
        if (roomSlot is not null)
        {
            if (imprint.EquippedSlotIndex != 1) return (null, "SoulImprintNotEquipped");
            var room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
            if (room is null) return (null, "NotFound");
            roomSlot.AutoPolicyOverridesJson = BattleAutoPolicyResolver.WithSoul(roomSlot, request.AutoUseEnabled, condition, threshold);
            room.Version++;
        }
        else
        {
            if (imprint.AutoUseEnabled == request.AutoUseEnabled && imprint.AutoConditionOverride == condition &&
                imprint.AutoHpThresholdPercent == threshold) return (await BuildResponseAsync(character!), null);
            imprint.AutoUseEnabled = request.AutoUseEnabled;
            imprint.AutoConditionOverride = condition;
            imprint.AutoHpThresholdPercent = threshold;
            imprint.Version++;
            character!.Version++;
        }
        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public Task<(CharacterSoulImprintsResponse? Response, string? Error)> DismantleAsync(
        string? token, int characterId, SoulImprintBatchRequest request) =>
        ExecuteInventoryBatchAsync(token, characterId, request);
    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        return await new CharacterAccessResolver(dbContext).OwnedAsync(user!, characterId);
    }

    private Task<string?> GetLoadoutLockErrorAsync(int characterId) =>
        CombatLoadoutMutationPolicy.LockErrorAsync(dbContext, characterId);

    private async Task<CharacterSoulImprintsResponse> BuildResponseAsync(Character character)
    {
        var imprints = await dbContext.CharacterSoulImprints.Where(item => item.CharacterId == character.Id)
            .OrderBy(item => item.EquippedSlotIndex == null).ThenBy(item => item.Id).ToListAsync();
        var fragmentStacks = await dbContext.CharacterItemStacks.Where(stack =>
            stack.CharacterId == character.Id && stack.ItemCode.StartsWith("weapon-fragment-t")).ToListAsync();
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == character.Id);
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
                var definition = catalog.Find(item.SoulImprintCode);
                if (definition is null) return InventoryItemProjection.Soul(item, catalog);
                var policy = roomSlot is not null && item.EquippedSlotIndex == 1
                    ? BattleAutoPolicyResolver.Soul(roomSlot, item)
                    : new(item.AutoUseEnabled, item.AutoConditionOverride, item.AutoHpThresholdPercent);
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
                    IsEquipped = item.EquippedSlotIndex == 1, AutoUseEnabled = policy.AutoUseEnabled,
                    DefaultAutoCondition = definition.AutoCondition,
                    AutoCondition = policy.AutoConditionOverride ?? definition.AutoCondition,
                    AutoConditionOverride = policy.AutoConditionOverride, AutoHpThresholdPercent = policy.AutoHpThresholdPercent,
                    IsLocked = item.IsLocked
                };
            }).ToList()
        };
    }
}
