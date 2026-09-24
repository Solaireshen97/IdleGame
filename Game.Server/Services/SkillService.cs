using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class SkillService(GameDbContext dbContext, UserService userService, SkillCatalog catalog)
{
    public SkillService(GameDbContext dbContext, UserService userService, SkillCatalog catalog, TalentService _)
        : this(dbContext, userService, catalog) { }
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

        var skill = request.SkillCode is null ? null : catalog.FindSkill(request.SkillCode.Trim());
        var purchasedNodes = await GetPurchasedNodeRanksAsync(characterId);
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

    public async Task<(CharacterSkillsResponse? Response, string? Error)> SetAutoAsync(
        string? token, int characterId, int slotIndex, SetSkillAutoRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (slotIndex < 1 || slotIndex > SkillRules.SlotCount) return (null, "InvalidSlotIndex");
        if (request.AutoHpThresholdPercent is < 1 or > 100) return (null, "InvalidHpThreshold");

        var target = await dbContext.CharacterSkillSlots.SingleOrDefaultAsync(slot =>
            slot.CharacterId == characterId && slot.SlotIndex == slotIndex);
        if (target?.SkillCode is null) return (null, "SkillNotEquipped");

        var purchasedNodes = await GetPurchasedNodeRanksAsync(characterId);
        if (!catalog.IsLearned(character!, target.SkillCode, purchasedNodes)) return (null, "SkillNotLearned");

        target.AutoUseEnabled = request.AutoUseEnabled;
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
        var purchasedNodes = await GetPurchasedNodeRanksAsync(characterId);
        var treePointsSpent = catalog.TalentNodesForProfession(character.ProfessionCode)
            .Sum(talent => purchasedNodes.GetValueOrDefault(talent.Code));
        var rank = purchasedNodes.GetValueOrDefault(node.Code);
        if (rank >= node.MaxRank) return (null, "SkillTalentMaxRank");
        if (character.Level < node.RequiredLevel + rank) return (null, "SkillTalentLevelRequired");
        if (treePointsSpent < node.RequiredTreePoints) return (null, "SkillTalentTreePointsRequired");
        if (node.Prerequisites.Any(code => catalog.FindTalentNode(code) is not { } parent ||
                purchasedNodes.GetValueOrDefault(code) < parent.MaxRank))
            return (null, "SkillTalentPrerequisiteRequired");
        if (node.ExclusiveGroup is not null && catalog.TalentNodesForProfession(character.ProfessionCode)
            .Any(other => !string.Equals(other.Code, node.Code, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.ExclusiveGroup, node.ExclusiveGroup, StringComparison.OrdinalIgnoreCase) &&
                purchasedNodes.GetValueOrDefault(other.Code) > 0)) return (null, "SkillTalentBranchLocked");
        if (character.TalentPoints < node.Cost) return (null, "InsufficientTalentPoints");

        var record = await dbContext.CharacterSkillTalents.SingleOrDefaultAsync(item => item.CharacterId == characterId && item.NodeCode == node.Code);
        if (record is null)
        {
            record = new CharacterSkillTalent { CharacterId = characterId, NodeCode = node.Code };
            dbContext.CharacterSkillTalents.Add(record);
        }
        record.PointsSpent++;
        character.TalentPoints -= node.Cost;
        ApplyTalentAggregates(character, node, 1);
        character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
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
        character!.TalentPoints += purchased.Sum(node => node.PointsSpent);
        character.TalentMaxHpPercent = character.TalentNormalAttackPercent = character.TalentSkillDamagePercent = 0;
        character.TalentHealingDonePercent = character.TalentHealingReceivedPercent = character.TalentSkillCriticalChancePercent = 0;
        character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
        dbContext.CharacterSkillTalents.RemoveRange(purchased);
        var alwaysLearned = catalog.LearnedSkills(character, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
            .Select(skill => skill.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slots = await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync();
        var removedSkillCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots.Where(slot => slot.SkillCode is not null && !alwaysLearned.Contains(slot.SkillCode)))
        {
            removedSkillCodes.Add(slot.SkillCode!);
            slot.SkillCode = null; slot.AutoUseEnabled = false; slot.Version++;
        }
        if (removedSkillCodes.Count > 0)
        {
            var cooldowns = await dbContext.BattleSkillCooldowns.Where(entry =>
                entry.CharacterId == characterId && removedSkillCodes.Contains(entry.SkillCode)).ToListAsync();
            dbContext.BattleSkillCooldowns.RemoveRange(cooldowns);
            var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
            if (roomSlot is not null)
            {
                roomSlot.PendingSkillSlotMask = 0;
                if (await dbContext.Rooms.FindAsync(roomSlot.RoomId) is { } room) room.Version++;
            }
        }
        character.Version++;
        try { await dbContext.SaveChangesAsync(); return (await BuildResponseAsync(character), null); }
        catch (DbUpdateException) { return (null, "ConcurrencyConflict"); }
    }

    public async Task<(CharacterSkillsResponse? Response, string? Error)> PromoteAsync(
        string? token, int characterId, PromoteCharacterRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (await IsLoadoutLockedAsync(characterId)) return (null, "LoadoutLocked");
        if (character!.AdvancedProfessionCode is not null) return (null, "AlreadyPromoted");
        if (character.Level < SkillRules.PromotionLevel) return (null, "PromotionLevelRequired");
        var promotion = catalog.FindProfession(request.ProfessionCode?.Trim());
        if (promotion is null || !promotion.IsPromotion ||
            !string.Equals(promotion.BaseProfessionCode, character.ProfessionCode, StringComparison.OrdinalIgnoreCase)) return (null, "InvalidPromotion");
        character.AdvancedProfessionCode = promotion.Code;
        var slots = await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync();
        foreach (var skillCode in promotion.StartingSkills)
        {
            if (slots.Any(slot => string.Equals(slot.SkillCode, skillCode, StringComparison.OrdinalIgnoreCase))) continue;
            var emptyIndex = Enumerable.Range(1, SkillRules.SlotCount).FirstOrDefault(index =>
                slots.All(slot => slot.SlotIndex != index) || slots.Any(slot => slot.SlotIndex == index && slot.SkillCode is null));
            if (emptyIndex == 0) continue;
            var target = slots.SingleOrDefault(slot => slot.SlotIndex == emptyIndex);
            if (target is null)
            {
                target = new CharacterSkillSlot { CharacterId = characterId, SlotIndex = emptyIndex,
                    AutoHpThresholdPercent = SkillRules.DefaultAutoHpThresholdPercent };
                slots.Add(target);
                dbContext.CharacterSkillSlots.Add(target);
            }
            target.SkillCode = skillCode;
            target.Version++;
        }
        character.Version++;
        try { await dbContext.SaveChangesAsync(); return (await BuildResponseAsync(character), null); }
        catch (DbUpdateException) { return (null, "ConcurrencyConflict"); }
    }

    private async Task<bool> IsLoadoutLockedAsync(int characterId)
    {
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        if (roomSlot is null) return false;
        var room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
        return room is not null && room.Status != RoomStatus.BattleOver &&
               (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0);
    }

    private async Task<Dictionary<string, int>> GetPurchasedNodeRanksAsync(int characterId) =>
        (await dbContext.CharacterSkillTalents.Where(node => node.CharacterId == characterId).ToListAsync())
        .ToDictionary(node => node.NodeCode, node => node.PointsSpent, StringComparer.OrdinalIgnoreCase);

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
        var advanced = catalog.FindProfession(character.AdvancedProfessionCode);
        var purchasedNodes = await GetPurchasedNodeRanksAsync(character.Id);
        var talentNodes = catalog.TalentNodesForProfession(profession.Code);
        var treePointsSpent = talentNodes.Sum(node => purchasedNodes.GetValueOrDefault(node.Code));
        var equipped = await dbContext.CharacterSkillSlots
            .Where(slot => slot.CharacterId == character.Id).ToDictionaryAsync(slot => slot.SlotIndex);
        return new CharacterSkillsResponse
        {
            CharacterId = character.Id,
            ProfessionCode = profession.Code,
            ProfessionName = profession.Name,
            AdvancedProfessionCode = advanced?.Code,
            AdvancedProfessionName = advanced?.Name,
            Level = character.Level,
            TalentPoints = character.TalentPoints,
            CanPromote = character.Level >= SkillRules.PromotionLevel && advanced is null,
            PromotionOptions = catalog.PromotionsFor(profession.Code).Select(item => new ProfessionResponse
            {
                Code = item.Code, Name = item.Name, Description = item.Description,
                GrantedSkillName = item.StartingSkills.Select(code => catalog.FindSkill(code)?.Name).FirstOrDefault()
            }).ToList(),
            LearnedSkills = catalog.LearnedSkills(character, purchasedNodes).Select(skill => new LearnedSkillResponse
            {
                Code = skill.Code,
                Name = skill.Name,
                Description = skill.Description,
                EffectType = SkillCatalog.PrimaryEffectType(skill),
                Power = SkillCatalog.PrimaryPower(skill),
                CooldownRounds = skill.CooldownRounds,
                AutoCondition = SkillCatalog.AutoConditionFor(skill),
                Effects = SkillCatalog.EffectsFor(skill).Select(effect => new SkillEffectResponse
                {
                    Type = effect.Type,
                    Target = effect.Target,
                    Power = effect.Power,
                    StatusCode = effect.StatusCode,
                    DurationRounds = effect.DurationRounds
                }).ToList()
            }).ToList(),
            TalentNodes = talentNodes.Select(node =>
            {
                var skill = catalog.FindSkill(node.SkillCode);
                var rank = purchasedNodes.GetValueOrDefault(node.Code);
                var prerequisitesMet = node.Prerequisites.All(code => catalog.FindTalentNode(code) is { } parent &&
                    purchasedNodes.GetValueOrDefault(code) >= parent.MaxRank);
                var branchOpen = node.ExclusiveGroup is null || !talentNodes.Any(other =>
                    !string.Equals(other.Code, node.Code, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(other.ExclusiveGroup, node.ExclusiveGroup, StringComparison.OrdinalIgnoreCase) &&
                    purchasedNodes.GetValueOrDefault(other.Code) > 0);
                var unlocked = rank > 0;
                return new SkillTalentNodeResponse
                {
                    Code = node.Code,
                    Name = node.Name,
                    Description = node.Description,
                    SkillCode = skill?.Code,
                    SkillName = skill?.Name ?? string.Empty,
                    SkillDescription = skill?.Description ?? string.Empty,
                    Cost = node.Cost,
                    Rank = rank,
                    MaxRank = node.MaxRank,
                    Tier = node.Tier,
                    Column = node.Column,
                    RequiredLevel = node.RequiredLevel,
                    BranchCode = node.BranchCode,
                    RequiredTreePoints = node.RequiredTreePoints,
                    ExclusiveGroup = node.ExclusiveGroup,
                    EffectCode = node.EffectCode,
                    ValuePerRank = node.ValuePerRank,
                    Prerequisites = [.. node.Prerequisites],
                    IsUnlocked = unlocked,
                    IsMaxRank = rank >= node.MaxRank,
                    ArePrerequisitesMet = prerequisitesMet,
                    CanUnlock = rank < node.MaxRank && character.Level >= node.RequiredLevel + rank &&
                        treePointsSpent >= node.RequiredTreePoints &&
                        prerequisitesMet && branchOpen && character.TalentPoints >= node.Cost
                };
            }).ToList(),
            Slots = Enumerable.Range(1, SkillRules.SlotCount).Select(index =>
            {
                equipped.TryGetValue(index, out var slot);
                var learned = slot?.SkillCode is not null && catalog.IsLearned(character, slot.SkillCode, purchasedNodes);
                return new EquippedSkillResponse
                {
                    SlotIndex = index,
                    SkillCode = learned ? slot!.SkillCode : null,
                    AutoUseEnabled = learned && slot!.AutoUseEnabled,
                    AutoHpThresholdPercent = slot?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent
                };
            }).ToList()
        };
    }

    private static void ApplyTalentAggregates(Character character, Game.Server.Configuration.SkillTalentNodeOptions node, int rankDelta)
    {
        var value = node.ValuePerRank * rankDelta;
        switch (node.EffectCode)
        {
            case "MaxHpPercent": character.TalentMaxHpPercent += value; break;
            case "NormalAttackPercent": character.TalentNormalAttackPercent += value; break;
            case "SkillDamagePercent": character.TalentSkillDamagePercent += value; break;
            case "HealingDonePercent": character.TalentHealingDonePercent += value; break;
            case "HealingReceivedPercent": character.TalentHealingReceivedPercent += value; break;
            case "SkillCriticalChancePercent": character.TalentSkillCriticalChancePercent += value; break;
        }
    }
}
