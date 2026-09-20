using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class SkillService(GameDbContext dbContext, UserService userService, SkillCatalog catalog)
{
    public List<ProfessionResponse> GetProfessions() => catalog.Professions
        .Select(profession => new ProfessionResponse
        {
            Code = profession.Code,
            Name = profession.Name,
            Description = profession.Description
        }).OrderBy(profession => profession.Code == SkillRules.DefaultProfessionCode ? 0 : 1)
          .ThenBy(profession => profession.Code).ToList();

    public async Task<(CharacterSkillsResponse? Response, string? Error)> GetAsync(string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        return error is null ? (await BuildResponseAsync(character!), null) : (null, error);
    }

    public async Task<(CharacterSkillsResponse? Response, string? Error)> SetSlotAsync(
        string? token, int characterId, int slotIndex, SetSkillSlotRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (slotIndex < 1 || slotIndex > SkillRules.SlotCount) return (null, "InvalidSlotIndex");
        if (request.AutoHpThresholdPercent is < 1 or > 100) return (null, "InvalidHpThreshold");

        var skill = request.SkillCode is null ? null : catalog.FindSkill(request.SkillCode.Trim());
        var purchasedNodes = await GetPurchasedNodeCodesAsync(characterId);
        if (request.SkillCode is not null && (skill is null || !catalog.IsLearned(character!, skill.Code, purchasedNodes)))
            return (null, "SkillNotLearned");

        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        Room? room = null;
        if (roomSlot is not null)
        {
            room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
            if (room is not null && room.Status != RoomStatus.BattleOver &&
                (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0))
                return (null, "LoadoutLocked");
        }

        var equipped = await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync();
        if (skill is not null && equipped.Any(slot => slot.SlotIndex != slotIndex &&
            string.Equals(slot.SkillCode, skill.Code, StringComparison.OrdinalIgnoreCase)))
            return (null, "SkillAlreadyEquipped");

        var target = equipped.SingleOrDefault(slot => slot.SlotIndex == slotIndex);
        if (target is null)
        {
            target = new CharacterSkillSlot { CharacterId = characterId, SlotIndex = slotIndex };
            dbContext.CharacterSkillSlots.Add(target);
        }
        else target.Version++;
        target.SkillCode = skill?.Code;
        target.AutoUseEnabled = skill is not null && request.AutoUseEnabled;
        target.AutoHpThresholdPercent = request.AutoHpThresholdPercent;
        character!.Version++;
        if (room is not null) room.Version++;
        if (roomSlot is not null) roomSlot.PendingSkillSlotMask &= ~SkillRules.SlotMask(slotIndex);

        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterSkillsResponse? Response, string? Error)> SwapSlotsAsync(
        string? token, int characterId, SwapSkillSlotsRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (request.FromSlotIndex is < 1 or > SkillRules.SlotCount ||
            request.ToSlotIndex is < 1 or > SkillRules.SlotCount) return (null, "InvalidSlotIndex");
        if (request.FromSlotIndex == request.ToSlotIndex) return (await BuildResponseAsync(character!), null);
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        Room? room = null;
        if (roomSlot is not null)
        {
            room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
            if (room is not null && room.Status != RoomStatus.BattleOver &&
                (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0)) return (null, "LoadoutLocked");
        }

        var entries = await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId &&
            (slot.SlotIndex == request.FromSlotIndex || slot.SlotIndex == request.ToSlotIndex)).ToListAsync();
        CharacterSkillSlot GetOrCreate(int index)
        {
            var slot = entries.SingleOrDefault(item => item.SlotIndex == index);
            if (slot is not null) return slot;
            slot = new CharacterSkillSlot { CharacterId = characterId, SlotIndex = index };
            dbContext.CharacterSkillSlots.Add(slot);
            return slot;
        }
        var from = GetOrCreate(request.FromSlotIndex);
        var to = GetOrCreate(request.ToSlotIndex);
        (from.SkillCode, to.SkillCode) = (to.SkillCode, from.SkillCode);
        (from.AutoUseEnabled, to.AutoUseEnabled) = (to.AutoUseEnabled, from.AutoUseEnabled);
        (from.AutoHpThresholdPercent, to.AutoHpThresholdPercent) = (to.AutoHpThresholdPercent, from.AutoHpThresholdPercent);
        from.Version++;
        to.Version++;
        character!.Version++;
        if (room is not null) room.Version++;
        if (roomSlot is not null)
            roomSlot.PendingSkillSlotMask &= ~(SkillRules.SlotMask(request.FromSlotIndex) | SkillRules.SlotMask(request.ToSlotIndex));
        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterSkillsResponse? Response, string? Error)> UnlockTalentNodeAsync(
        string? token, int characterId, string nodeCode)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var node = catalog.FindTalentNode(nodeCode);
        if (node is null || !string.Equals(node.ProfessionCode, character!.ProfessionCode, StringComparison.OrdinalIgnoreCase))
            return (null, "InvalidSkillTalent");
        if (await IsLoadoutLockedAsync(characterId)) return (null, "LoadoutLocked");
        var purchasedNodes = await GetPurchasedNodeCodesAsync(characterId);
        if (purchasedNodes.Contains(node.Code)) return (null, "SkillTalentAlreadyUnlocked");
        if (node.Prerequisites.Any(code => !purchasedNodes.Contains(code))) return (null, "SkillTalentPrerequisiteRequired");
        if (character.TalentPoints < node.Cost) return (null, "InsufficientTalentPoints");

        dbContext.CharacterSkillTalents.Add(new CharacterSkillTalent
        {
            CharacterId = characterId,
            NodeCode = node.Code,
            PointsSpent = node.Cost
        });
        character.TalentPoints -= node.Cost;
        character.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterSkillsResponse? Response, string? Error)> ResetTalentTreeAsync(string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (await IsLoadoutLockedAsync(characterId)) return (null, "LoadoutLocked");
        var purchased = await dbContext.CharacterSkillTalents.Where(node => node.CharacterId == characterId).ToListAsync();
        if (purchased.Count == 0) return (await BuildResponseAsync(character!), null);

        var equipped = await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync();
        var noPurchasedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedSkillCodes = purchased.Select(node => catalog.FindTalentNode(node.NodeCode)?.SkillCode)
            .Where(code => code is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        removedSkillCodes.UnionWith(equipped.Where(slot => slot.SkillCode is not null &&
                !catalog.IsLearned(character!, slot.SkillCode, noPurchasedNodes))
            .Select(slot => slot.SkillCode!));
        foreach (var slot in equipped.Where(slot => slot.SkillCode is not null &&
                     !catalog.IsLearned(character!, slot.SkillCode, noPurchasedNodes)))
        {
            slot.SkillCode = null;
            slot.AutoUseEnabled = false;
            slot.Version++;
        }
        var cooldowns = await dbContext.BattleSkillCooldowns
            .Where(entry => entry.CharacterId == characterId && removedSkillCodes.Contains(entry.SkillCode)).ToListAsync();
        dbContext.BattleSkillCooldowns.RemoveRange(cooldowns);
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        if (roomSlot is not null)
        {
            roomSlot.PendingSkillSlotMask = 0;
            var room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
            if (room is not null) room.Version++;
        }
        dbContext.CharacterSkillTalents.RemoveRange(purchased);
        character!.TalentPoints = checked(character.TalentPoints + purchased.Sum(node => node.PointsSpent));
        character.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    private async Task<bool> IsLoadoutLockedAsync(int characterId)
    {
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        if (roomSlot is null) return false;
        var room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
        return room is not null && room.Status != RoomStatus.BattleOver &&
               (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0);
    }

    private async Task<HashSet<string>> GetPurchasedNodeCodesAsync(int characterId) =>
        (await dbContext.CharacterSkillTalents.Where(node => node.CharacterId == characterId)
            .Select(node => node.NodeCode).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var character = await dbContext.Characters.FirstOrDefaultAsync(item => item.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user!.Id ? (character, null) : (null, "NotOwner");
    }

    private async Task<CharacterSkillsResponse> BuildResponseAsync(Character character)
    {
        var profession = catalog.FindProfession(character.ProfessionCode)!;
        var purchasedNodes = await GetPurchasedNodeCodesAsync(character.Id);
        var equipped = await dbContext.CharacterSkillSlots
            .Where(slot => slot.CharacterId == character.Id).ToDictionaryAsync(slot => slot.SlotIndex);
        return new CharacterSkillsResponse
        {
            CharacterId = character.Id,
            ProfessionCode = profession.Code,
            ProfessionName = profession.Name,
            TalentPoints = character.TalentPoints,
            LearnedSkills = catalog.LearnedSkills(character, purchasedNodes).Select(skill => new LearnedSkillResponse
            {
                Code = skill.Code,
                Name = skill.Name,
                Description = skill.Description,
                EffectType = skill.EffectType,
                Power = skill.Power,
                CooldownRounds = skill.CooldownRounds
            }).ToList(),
            TalentNodes = catalog.TalentNodesForProfession(profession.Code).Select(node =>
            {
                var skill = catalog.FindSkill(node.SkillCode)!;
                var prerequisitesMet = node.Prerequisites.All(purchasedNodes.Contains);
                var unlocked = purchasedNodes.Contains(node.Code);
                return new SkillTalentNodeResponse
                {
                    Code = node.Code,
                    Name = node.Name,
                    Description = node.Description,
                    SkillCode = skill.Code,
                    SkillName = skill.Name,
                    SkillDescription = skill.Description,
                    Cost = node.Cost,
                    Tier = node.Tier,
                    Column = node.Column,
                    Prerequisites = [.. node.Prerequisites],
                    IsUnlocked = unlocked,
                    ArePrerequisitesMet = prerequisitesMet,
                    CanUnlock = !unlocked && prerequisitesMet && character.TalentPoints >= node.Cost
                };
            }).ToList(),
            Slots = Enumerable.Range(1, SkillRules.SlotCount).Select(index =>
            {
                equipped.TryGetValue(index, out var slot);
                return new EquippedSkillResponse
                {
                    SlotIndex = index,
                    SkillCode = slot?.SkillCode,
                    AutoUseEnabled = slot?.AutoUseEnabled ?? false,
                    AutoHpThresholdPercent = slot?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent
                };
            }).ToList()
        };
    }
}
