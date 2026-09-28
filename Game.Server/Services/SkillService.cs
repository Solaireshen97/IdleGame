using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class SkillService(GameDbContext dbContext, UserService userService, SkillCatalog catalog)
{
    public List<ProfessionResponse> GetProfessions() => catalog.BaseProfessions
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
        var autoCondition = SkillAutoRules.Normalize(request.AutoConditionOverride);
        if (!SkillAutoRules.IsValidOverride(autoCondition)) return (null, "InvalidAutoCondition");

        var skill = request.SkillCode is null ? null : catalog.FindSkill(request.SkillCode.Trim());
        var professionLevels = await GetProfessionLevelsAsync(characterId);
        if (request.SkillCode is not null && (skill is null || !catalog.IsLearned(character!, skill.Code,
                new Dictionary<string, int>(), professionLevels)))
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
        if (skill is not null && !string.Equals(skill.ProfessionCode, character!.ProfessionCode, StringComparison.OrdinalIgnoreCase) &&
            equipped.Any(slot => slot.SlotIndex != slotIndex && slot.SkillCode is not null &&
                catalog.FindSkill(slot.SkillCode) is { } other &&
                !string.Equals(other.ProfessionCode, character.ProfessionCode, StringComparison.OrdinalIgnoreCase)))
            return (null, "SharedSkillLimitReached");

        var target = equipped.SingleOrDefault(slot => slot.SlotIndex == slotIndex);
        if (target is null)
        {
            target = new CharacterSkillSlot { CharacterId = characterId, SlotIndex = slotIndex };
            dbContext.CharacterSkillSlots.Add(target);
        }
        else target.Version++;
        target.SkillCode = skill?.Code;
        target.AutoUseEnabled = skill is not null && request.AutoUseEnabled;
        target.AutoConditionOverride = skill is null ? null : autoCondition;
        target.AutoHpThresholdPercent = request.AutoHpThresholdPercent;
        character!.Version++;
        if (room is not null) room.Version++;
        if (roomSlot is not null) SkillQueueRules.Clear(roomSlot, SkillRules.SlotMask(slotIndex));

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

    public async Task<(CharacterSkillsResponse? Response, string? Error)> SetAutoAsync(
        string? token, int characterId, int slotIndex, SetSkillAutoRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (slotIndex < 1 || slotIndex > SkillRules.SlotCount) return (null, "InvalidSlotIndex");
        if (request.AutoHpThresholdPercent is < 1 or > 100) return (null, "InvalidHpThreshold");
        var autoCondition = SkillAutoRules.Normalize(request.AutoConditionOverride);
        if (!SkillAutoRules.IsValidOverride(autoCondition)) return (null, "InvalidAutoCondition");

        var target = await dbContext.CharacterSkillSlots.SingleOrDefaultAsync(slot =>
            slot.CharacterId == characterId && slot.SlotIndex == slotIndex);
        if (target?.SkillCode is null) return (null, "SkillNotEquipped");

        var professionLevels = await GetProfessionLevelsAsync(characterId);
        if (!catalog.IsLearned(character!, target.SkillCode, new Dictionary<string, int>(), professionLevels))
            return (null, "SkillNotLearned");

        target.AutoUseEnabled = request.AutoUseEnabled;
        target.AutoConditionOverride = autoCondition;
        target.AutoHpThresholdPercent = request.AutoHpThresholdPercent;
        target.Version++;
        character!.Version++;

        try
        {
            await dbContext.SaveChangesAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateConcurrencyException)
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
        (from.AutoConditionOverride, to.AutoConditionOverride) = (to.AutoConditionOverride, from.AutoConditionOverride);
        (from.AutoHpThresholdPercent, to.AutoHpThresholdPercent) = (to.AutoHpThresholdPercent, from.AutoHpThresholdPercent);
        from.Version++;
        to.Version++;
        character!.Version++;
        if (room is not null) room.Version++;
        if (roomSlot is not null)
            SkillQueueRules.Clear(roomSlot, SkillRules.SlotMask(request.FromSlotIndex) | SkillRules.SlotMask(request.ToSlotIndex));
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

    private async Task<Dictionary<string, int>> GetProfessionLevelsAsync(int characterId) =>
        (await dbContext.CharacterCombatProfessions.AsNoTracking()
            .Where(progress => progress.CharacterId == characterId).ToListAsync())
            .ToDictionary(progress => progress.ProfessionCode, progress => progress.Level, StringComparer.OrdinalIgnoreCase);

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
        var professionLevels = await GetProfessionLevelsAsync(character.Id);
        professionLevels[profession.Code] = character.Level;
        var equipped = await dbContext.CharacterSkillSlots
            .Where(slot => slot.CharacterId == character.Id).ToDictionaryAsync(slot => slot.SlotIndex);
        LearnedSkillResponse Describe(Game.Server.Configuration.CombatSkillOptions skill, bool isShared) => new()
        {
            Code = skill.Code, Name = skill.Name, Description = skill.Description,
            EffectType = SkillCatalog.PrimaryEffectType(skill), Power = SkillCatalog.PrimaryPower(skill),
            CooldownRounds = skill.CooldownRounds, InitialCooldownRounds = skill.InitialCooldownRounds,
            Level = isShared ? 3 : SkillCatalog.RankFor(skill, character.Level),
            UnlockLevel = skill.UnlockLevel, Level2UnlockLevel = skill.Level2UnlockLevel,
            Level3UnlockLevel = skill.Level3UnlockLevel, IsShared = isShared,
            SourceProfessionCode = skill.ProfessionCode, AutoCondition = SkillCatalog.AutoConditionFor(skill),
            Effects = SkillCatalog.EffectsFor(skill).Select(effect => new SkillEffectResponse
            {
                Type = effect.Type, Target = effect.Target, Power = effect.Power,
                AttackPowerPercent = effect.AttackPowerPercent, HealMaxHpPercent = effect.HealMaxHpPercent,
                StatusCode = effect.StatusCode, DurationRounds = effect.DurationRounds
            }).ToList()
        };
        SharedSkillResponse DescribeShared(Game.Server.Configuration.CombatSkillOptions skill)
        {
            var source = Describe(skill, true);
            return new SharedSkillResponse
            {
                Code = source.Code, Name = source.Name, Description = source.Description,
                EffectType = source.EffectType, Power = source.Power, CooldownRounds = source.CooldownRounds,
                InitialCooldownRounds = source.InitialCooldownRounds, Level = source.Level,
                UnlockLevel = source.UnlockLevel, Level2UnlockLevel = source.Level2UnlockLevel,
                Level3UnlockLevel = source.Level3UnlockLevel, IsShared = true,
                SourceProfessionCode = source.SourceProfessionCode, AutoCondition = source.AutoCondition,
                Effects = source.Effects, CanEquip = character.Level >= SkillRules.SharedSkillEquipLevel
            };
        }
        return new CharacterSkillsResponse
        {
            CharacterId = character.Id,
            ProfessionCode = profession.Code,
            ProfessionName = profession.Name,
            Level = character.Level,
            LearnedSkills = catalog.LearnedSkills(character, new Dictionary<string, int>()).Select(skill => Describe(skill, false)).ToList(),
            SharedSkills = catalog.SharedSkills(character, professionLevels).Select(DescribeShared).ToList(),
            Slots = Enumerable.Range(1, SkillRules.SlotCount).Select(index =>
            {
                equipped.TryGetValue(index, out var slot);
                var resolved = catalog.ResolveSkillForLevel(character, slot?.SkillCode, professionLevels);
                var learned = resolved is not null;
                return new EquippedSkillResponse
                {
                    SlotIndex = index,
                    SkillCode = learned ? slot!.SkillCode : null,
                    AutoUseEnabled = learned && slot!.AutoUseEnabled,
                    AutoCondition = learned ? slot!.AutoConditionOverride ?? SkillCatalog.AutoConditionFor(resolved!) : "Always",
                    AutoConditionOverride = learned ? slot!.AutoConditionOverride : null,
                    AutoHpThresholdPercent = slot?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent
                };
            }).ToList()
        };
    }

}
