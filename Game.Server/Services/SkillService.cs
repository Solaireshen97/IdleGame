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
        if (request.SkillCode is not null && (skill is null || !catalog.IsLearned(character!, skill.Code)))
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
        var equipped = await dbContext.CharacterSkillSlots
            .Where(slot => slot.CharacterId == character.Id).ToDictionaryAsync(slot => slot.SlotIndex);
        return new CharacterSkillsResponse
        {
            CharacterId = character.Id,
            ProfessionCode = profession.Code,
            ProfessionName = profession.Name,
            LearnedSkills = catalog.LearnedSkills(character).Select(skill => new LearnedSkillResponse
            {
                Code = skill.Code,
                Name = skill.Name,
                Description = skill.Description,
                EffectType = skill.EffectType,
                Power = skill.Power,
                CooldownRounds = skill.CooldownRounds
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
