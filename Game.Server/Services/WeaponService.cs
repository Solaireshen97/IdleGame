using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class WeaponService(GameDbContext dbContext, UserService userService, SkillCatalog skillCatalog, WeaponCatalog weaponCatalog,
    WeaponBreakthroughCatalog? breakthroughCatalog = null)
{
    private readonly WeaponBreakthroughCatalog _breakthroughCatalog = breakthroughCatalog ??
        new WeaponBreakthroughCatalog(Microsoft.Extensions.Options.Options.Create(new Game.Server.Configuration.WeaponBreakthroughOptions()));
    public async Task<(CharacterWeaponsResponse? Response, string? Error)> GetAsync(string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        return error is null ? (await BuildResponseAsync(character!), null) : (null, error);
    }

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> SetSlotAsync(
        string? token, int characterId, int slotIndex, SetWeaponSlotRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (slotIndex is < 1 or > WeaponRules.SlotCount) return (null, "InvalidSlotIndex");
        if (slotIndex == WeaponRules.MainSlotIndex && request.WeaponId is null) return (null, "MainWeaponRequired");
        if (await GetArmoryLockErrorAsync(characterId) is { } lockError) return (null, lockError);

        var weapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
            .Where(weapon => weapon.CharacterId == characterId).ToListAsync();
        var selected = request.WeaponId is int id ? weapons.SingleOrDefault(weapon => weapon.Id == id) : null;
        if (request.WeaponId is not null && selected is null) return (null, "WeaponNotOwned");
        var displaced = weapons.SingleOrDefault(weapon => weapon.EquippedSlotIndex == slotIndex);
        if (selected?.EquippedSlotIndex == WeaponRules.MainSlotIndex &&
            slotIndex != WeaponRules.MainSlotIndex && displaced is null) return (null, "MainWeaponRequired");
        if (selected?.Id == displaced?.Id || selected is null && displaced is null)
            return (await BuildResponseAsync(character!), null);

        var previousSlot = selected?.EquippedSlotIndex;
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            // Release the two unique slot values before assigning their new owners.
            if (selected?.EquippedSlotIndex is not null)
            {
                selected.EquippedSlotIndex = null;
                selected.Version++;
                await dbContext.SaveChangesAsync();
            }
            if (displaced is not null)
            {
                displaced.EquippedSlotIndex = previousSlot;
                displaced.Version++;
                await dbContext.SaveChangesAsync();
            }
            if (selected is not null)
            {
                selected.EquippedSlotIndex = slotIndex;
                selected.Version++;
            }

            RecalculateCharacter(character!, weapons);
            var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(roomSlot => roomSlot.CharacterId == characterId);
            if (roomSlot is not null && await dbContext.Rooms.FindAsync(roomSlot.RoomId) is { } room) room.Version++;
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> SetLockAsync(
        string? token, int characterId, int weaponId, SetWeaponLockRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var weapon = await dbContext.CharacterWeapons.SingleOrDefaultAsync(item =>
            item.Id == weaponId && item.CharacterId == characterId);
        if (weapon is null) return (null, "WeaponNotOwned");
        if (weapon.IsLocked == request.IsLocked) return (await BuildResponseAsync(character!), null);
        weapon.IsLocked = request.IsLocked;
        weapon.Version++;
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

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> SellAsync(
        string? token, int characterId, WeaponBatchRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (!TryValidateSelection(request.WeaponIds, out var ids)) return (null, "InvalidWeaponSelection");
        if (await GetArmoryLockErrorAsync(characterId) is { } lockError) return (null, lockError);
        var weapons = await LoadSelectedWeaponsAsync(characterId, ids);
        if (weapons.Count != ids.Count) return (null, "WeaponNotOwned");
        if (weapons.Any(weapon => weapon.EquippedSlotIndex.HasValue)) return (null, "WeaponEquipped");
        if (weapons.Any(weapon => weapon.IsLocked)) return (null, "WeaponLocked");
        if (await FormationItemReferencePolicy.WeaponsAsync(dbContext, characterId, ids) is { } referenceError)
            return (null, referenceError);
        var gold = weapons.Sum(weapon => weapon.SellGold);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            character!.Gold = checked(character.Gold + gold);
            character!.Version++;
            dbContext.CharacterWeapons.RemoveRange(weapons);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> DismantleAsync(
        string? token, int characterId, WeaponBatchRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (!TryValidateSelection(request.WeaponIds, out var ids)) return (null, "InvalidWeaponSelection");
        if (await GetArmoryLockErrorAsync(characterId) is { } lockError) return (null, lockError);
        var weapons = await LoadSelectedWeaponsAsync(characterId, ids);
        if (weapons.Count != ids.Count) return (null, "WeaponNotOwned");
        if (weapons.Any(weapon => weapon.EquippedSlotIndex.HasValue)) return (null, "WeaponEquipped");
        if (weapons.Any(weapon => weapon.IsLocked)) return (null, "WeaponLocked");
        if (await FormationItemReferencePolicy.WeaponsAsync(dbContext, characterId, ids) is { } referenceError)
            return (null, referenceError);
        if (weapons.Any(weapon => !weaponCatalog.CanDismantle(weapon)))
            return (null, "WeaponCannotBeDismantled");
        var returns = weapons.GroupBy(weapon => WeaponRules.FragmentTier(weapon.ItemLevel))
            .ToDictionary(group => group.Key, group => group.Sum(weaponCatalog.DismantleReturn));
        var codes = returns.Keys.Select(WeaponRules.FragmentCode).ToList();
        var stacks = await dbContext.CharacterItemStacks.Where(stack =>
            stack.CharacterId == characterId && codes.Contains(stack.ItemCode)).ToListAsync();

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            foreach (var (tier, quantity) in returns.Where(pair => pair.Value > 0))
            {
                var code = WeaponRules.FragmentCode(tier);
                var stack = stacks.SingleOrDefault(item => item.ItemCode == code);
                if (stack is null)
                {
                    stack = new CharacterItemStack { CharacterId = characterId, ItemCode = code };
                    dbContext.CharacterItemStacks.Add(stack);
                }
                else stack.Version++;
                stack.Quantity = checked(stack.Quantity + quantity);
            }
            character!.Version++;
            dbContext.CharacterWeapons.RemoveRange(weapons);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> EnhanceSkillAsync(
        string? token, int characterId, int weaponId, int skillSlotIndex, string? requestId = null)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (!Guid.TryParse(requestId, out var requestGuid) || requestGuid == Guid.Empty)
            return (null, "InvalidRequestId");
        requestId = requestGuid.ToString("N");
        var fingerprint = $"{weaponId}:{skillSlotIndex}";
        var previous = await dbContext.LogisticsRequests.AsNoTracking().SingleOrDefaultAsync(receipt =>
            receipt.CharacterId == characterId && receipt.RequestId == requestId);
        if (previous is not null)
        {
            if (previous.Kind != "WeaponEnhancement" || previous.Fingerprint != fingerprint)
                return (null, "RequestIdReused");
            dbContext.ChangeTracker.Clear();
            return await GetAsync(token, characterId);
        }
        if (await GetArmoryLockErrorAsync(characterId) is { } lockError) return (null, lockError);
        var weapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
            .Where(weapon => weapon.CharacterId == characterId).ToListAsync();
        var weapon = weapons.SingleOrDefault(item => item.Id == weaponId);
        if (weapon is null) return (null, "WeaponNotOwned");
        var skill = weapon.Skills.SingleOrDefault(item => item.SlotIndex == skillSlotIndex);
        if (skill is null) return (null, "WeaponSkillNotFound");
        if (skill.EnhancementLevel >= WeaponRules.EnhancementLimit(weapon.QualityRank, skill.BaseLevel))
            return (null, "WeaponSkillAtMaximum");
        var tier = WeaponRules.FragmentTier(weapon.ItemLevel);
        var fragmentCode = WeaponRules.FragmentCode(tier);
        var cost = weaponCatalog.EnhancementCost(skill.EnhancementLevel);
        var stack = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(item =>
            item.CharacterId == characterId && item.ItemCode == fragmentCode);
        if (stack is null || stack.Quantity < cost) return (null, "InsufficientWeaponFragments");

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            stack.Quantity -= cost;
            stack.Version++;
            skill.SpentFragments = checked(weaponCatalog.InvestedFragments(skill) + cost);
            skill.EnhancementLevel++;
            skill.Level++;
            weapon.Version++;
            RecalculateCharacter(character!, weapons);
            dbContext.LogisticsRequests.Add(new LogisticsRequest { CharacterId = characterId, RequestId = requestId,
                Kind = "WeaponEnhancement", Fingerprint = fingerprint, CompletedAtUtc = DateTime.UtcNow });
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
    }

    public Task<(CharacterWeaponsResponse? Response, string? Error)> UpgradeQualityAsync(
        string? token, int characterId, int weaponId, int materialWeaponId)
        => UpgradeQualityAsync(token, characterId, weaponId,
            new UpgradeWeaponQualityRequest { MaterialWeaponId = materialWeaponId });

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> UpgradeQualityAsync(
        string? token, int characterId, int weaponId, UpgradeWeaponQualityRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var materialWeaponId = request.MaterialWeaponId;
        if (request.UseUniversalStone ? materialWeaponId != 0 : weaponId == materialWeaponId || materialWeaponId <= 0)
            return (null, "InvalidQualityMaterial");
        if (await GetArmoryLockErrorAsync(characterId) is { } lockError) return (null, lockError);
        var weapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
            .Where(weapon => weapon.CharacterId == characterId).ToListAsync();
        var target = weapons.SingleOrDefault(weapon => weapon.Id == weaponId);
        var material = weapons.SingleOrDefault(weapon => weapon.Id == materialWeaponId);
        if (target is null || !request.UseUniversalStone && material is null) return (null, "WeaponNotOwned");
        if (target.QualityRank >= WeaponRules.MaxQualityBonusLevels) return (null, "WeaponQualityAtMaximum");
        CharacterItemStack? stoneStack = null;
        if (request.UseUniversalStone)
        {
            var recipe = _breakthroughCatalog.FindForWeapon(target.ItemLevel);
            if (recipe is null) return (null, "WeaponBreakthroughStageUnsupported");
            stoneStack = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(stack =>
                stack.CharacterId == characterId && stack.ItemCode == recipe.StoneCode);
            if (stoneStack is null || stoneStack.Quantity < 1) return (null, "InsufficientBreakthroughStones");
        }
        else
        {
            if (!string.Equals(weaponCatalog.FindItem(target.WeaponCode)?.Code ?? target.WeaponCode,
                    weaponCatalog.FindItem(material!.WeaponCode)?.Code ?? material.WeaponCode,
                    StringComparison.OrdinalIgnoreCase)) return (null, "QualityMaterialMustMatch");
            if (material.EquippedSlotIndex.HasValue) return (null, "WeaponEquipped");
            if (material.IsLocked) return (null, "WeaponLocked");
            if (await FormationItemReferencePolicy.WeaponsAsync(dbContext, characterId, [material.Id]) is { } referenceError)
                return (null, referenceError);
            if (material.Origin == WeaponOrigin.Starter) return (null, "StarterWeaponCannotBeConsumed");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            target.QualityRank++;
            weaponCatalog.UnlockSkillsForQuality(target);
            target.Version++;
            character!.Version++;
            if (stoneStack is not null)
            {
                stoneStack.Quantity--;
                stoneStack.Version++;
            }
            else dbContext.CharacterWeapons.Remove(material!);
            if (target.EquippedSlotIndex.HasValue) RecalculateCharacter(character!, weapons.Where(weapon => weapon != material).ToList());
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> CraftBreakthroughStoneAsync(
        string? token, int characterId, CraftWeaponBreakthroughStoneRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var recipe = _breakthroughCatalog.FindTier(request.Tier);
        if (recipe is null || request.Quantity <= 0) return (null, "InvalidBreakthroughCraftRequest");
        var cost = (long)recipe.FragmentsPerStone * request.Quantity;
        if (cost > int.MaxValue) return (null, "InvalidBreakthroughCraftRequest");
        if (await GetArmoryLockErrorAsync(characterId) is { } lockError) return (null, lockError);
        var stacks = await dbContext.CharacterItemStacks.Where(stack => stack.CharacterId == characterId &&
            (stack.ItemCode == recipe.FragmentCode || stack.ItemCode == recipe.StoneCode)).ToListAsync();
        var fragments = stacks.SingleOrDefault(stack => stack.ItemCode == recipe.FragmentCode);
        if (fragments is null || fragments.Quantity < cost) return (null, "InsufficientBreakthroughFragments");
        var stones = stacks.SingleOrDefault(stack => stack.ItemCode == recipe.StoneCode);
        if ((long)(stones?.Quantity ?? 0) + request.Quantity > int.MaxValue)
            return (null, "InvalidBreakthroughCraftRequest");

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            fragments.Quantity -= (int)cost;
            fragments.Version++;
            if (stones is null)
            {
                stones = new CharacterItemStack { CharacterId = characterId, ItemCode = recipe.StoneCode };
                dbContext.CharacterItemStacks.Add(stones);
            }
            else stones.Version++;
            stones.Quantity += request.Quantity;
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

    private static bool TryValidateSelection(IReadOnlyCollection<int>? weaponIds, out List<int> ids)
    {
        ids = weaponIds?.Distinct().ToList() ?? [];
        return ids.Count is > 0 and <= 100 && ids.Count == weaponIds!.Count && ids.All(id => id > 0);
    }

    private Task<List<CharacterWeapon>> LoadSelectedWeaponsAsync(int characterId, IReadOnlyCollection<int> ids) =>
        dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
            .Where(weapon => weapon.CharacterId == characterId && ids.Contains(weapon.Id)).ToListAsync();

    private Task<string?> GetArmoryLockErrorAsync(int characterId) =>
        CombatLoadoutMutationPolicy.LockErrorAsync(dbContext, characterId);

    private void RecalculateCharacter(Character character, IReadOnlyCollection<CharacterWeapon> weapons)
    {
        weaponCatalog.RecalculateEquipmentStats(character, weapons);
        character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
        character.Version++;
    }

    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        return await new CharacterAccessResolver(dbContext).OwnedAsync(user!, characterId);
    }

    private async Task<CharacterWeaponsResponse> BuildResponseAsync(Character character)
    {
        var weapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
            .Where(item => item.CharacterId == character.Id)
            .OrderBy(item => item.EquippedSlotIndex == null).ThenBy(item => item.EquippedSlotIndex)
            .ThenByDescending(item => item.ItemLevel).ThenBy(item => item.Id).ToListAsync();
        var fragmentStacks = await dbContext.CharacterItemStacks.Where(stack =>
            stack.CharacterId == character.Id && stack.ItemCode.StartsWith("weapon-fragment-t")).ToListAsync();
        var breakthroughCodes = _breakthroughCatalog.Recipes.SelectMany(recipe => new[] { recipe.FragmentCode, recipe.StoneCode }).ToList();
        var breakthroughStacks = await dbContext.CharacterItemStacks.Where(stack =>
            stack.CharacterId == character.Id && breakthroughCodes.Contains(stack.ItemCode)).ToListAsync();
        var main = weapons.SingleOrDefault(item => item.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        var bonuses = weaponCatalog.CalculateBonuses(weapons);
        var highestStackTier = fragmentStacks.Select(stack => ParseFragmentTier(stack.ItemCode)).DefaultIfEmpty(1).Max();
        var maximumTier = Math.Max(weaponCatalog.MaxFragmentTier, highestStackTier);
        return new CharacterWeaponsResponse
        {
            CharacterId = character.Id,
            CharacterName = character.Name,
            ProfessionName = skillCatalog.EffectiveProfession(character)?.Name ?? character.ProfessionCode,
            Gold = character.Gold,
            Hp = character.Hp,
            TotalAttack = character.Attack,
            TotalMaxHp = character.MaxHp,
            EffectiveAttack = TalentRules.EffectiveAttack(character),
            EffectiveMaxHp = TalentRules.EffectiveMaxHp(character),
            MainElement = main?.Element,
            AttackBonusPercent = bonuses.AttackPercent,
            HealthBonusPercent = bonuses.HealthPercent,
            CriticalChancePercent = bonuses.CriticalChancePercent,
            ActiveSkills = bonuses.ActiveSkills.Select(skill => new ActiveWeaponSkillResponse
            {
                SkillCode = skill.Code, Name = skill.Name, Level = skill.Level,
                TotalPercent = skill.TotalPercent, Description = skill.Description
            }).ToList(),
            ActiveEffects = bonuses.Effects.Select(effect => new WeaponEffectResponse
            {
                EffectType = effect.EffectType, Name = WeaponEffectLabels.Name(effect.EffectType),
                Description = WeaponEffectLabels.Description(effect.EffectType),
                EffectiveLevel = effect.EffectiveLevel, TotalPercent = effect.TotalPercent
            }).ToList(),
            Fragments = Enumerable.Range(1, maximumTier).Select(tier => new WeaponFragmentResponse
            {
                Tier = tier,
                Code = WeaponRules.FragmentCode(tier),
                Name = WeaponRules.FragmentName(tier),
                Quantity = fragmentStacks.SingleOrDefault(stack => stack.ItemCode == WeaponRules.FragmentCode(tier))?.Quantity ?? 0
            }).ToList(),
            BreakthroughMaterials = _breakthroughCatalog.Recipes.Select(recipe =>
            {
                var fragments = breakthroughStacks.SingleOrDefault(stack => stack.ItemCode == recipe.FragmentCode)?.Quantity ?? 0;
                var stones = breakthroughStacks.SingleOrDefault(stack => stack.ItemCode == recipe.StoneCode)?.Quantity ?? 0;
                return new WeaponBreakthroughMaterialResponse
                {
                    Tier = recipe.Tier, FragmentCode = recipe.FragmentCode, FragmentName = $"T{recipe.Tier} 通用突破碎片",
                    FragmentQuantity = fragments, StoneCode = recipe.StoneCode, StoneName = $"T{recipe.Tier} 通用突破石",
                    StoneQuantity = stones, FragmentsPerStone = recipe.FragmentsPerStone,
                    CanCraftQuantity = Math.Min(fragments / recipe.FragmentsPerStone, int.MaxValue - stones)
                };
            }).ToList(),
            Weapons = weapons.Select(item => new CharacterWeaponResponse
            {
                Id = item.Id,
                WeaponCode = item.WeaponCode,
                Name = item.Name,
                Element = item.Element,
                Attack = item.Attack,
                MaxHp = item.MaxHp,
                ItemLevel = item.ItemLevel,
                FragmentTier = WeaponRules.FragmentTier(item.ItemLevel),
                BreakthroughTier = _breakthroughCatalog.FindForWeapon(item.ItemLevel)?.Tier,
                SellGold = item.SellGold,
                CanSell = true,
                Origin = item.Origin,
                DismantleFragments = WeaponCatalog.BaseDismantleReturn(item),
                DismantleReturnQuantity = weaponCatalog.DismantleReturn(item),
                CanDismantle = weaponCatalog.CanDismantle(item),
                QualityRank = item.QualityRank,
                QualityName = WeaponRules.QualityName(item.QualityRank),
                QualityCode = WeaponRules.QualityCode(item.QualityRank),
                IsLocked = item.IsLocked,
                EquippedSlotIndex = item.EquippedSlotIndex,
                LockedSkills = (weaponCatalog.FindItem(item.WeaponCode)?.Skills ?? [])
                    .Where((grant, index) => grant.UnlockQualityRank > item.QualityRank &&
                        item.Skills.All(skill => skill.SlotIndex != index + 1))
                    .Select(grant => new LockedWeaponSkillResponse
                    {
                        Name = weaponCatalog.FindSkill(grant.Code)?.Name ?? grant.Code,
                        UnlockQualityRank = grant.UnlockQualityRank,
                        Description = weaponCatalog.FindSkill(grant.Code) is { } definition
                            ? weaponCatalog.DescribeSkill(definition, grant.Level) : string.Empty
                    }).ToList(),
                Skills = item.Skills.OrderBy(skill => skill.SlotIndex).Select(skill =>
                {
                    var definition = weaponCatalog.FindSkill(skill.SkillCode);
                    return new WeaponSkillResponse
                    {
                        SlotIndex = skill.SlotIndex,
                        SkillCode = skill.SkillCode,
                        Name = definition?.Name ?? skill.SkillCode,
                        Level = skill.Level,
                        BaseLevel = skill.BaseLevel,
                        EnhancementLevel = skill.EnhancementLevel,
                        MaximumEnhancementLevel = WeaponRules.EnhancementLimit(item.QualityRank, skill.BaseLevel),
                        NextEnhancementCost = skill.EnhancementLevel < WeaponRules.EnhancementLimit(item.QualityRank, skill.BaseLevel)
                            ? weaponCatalog.EnhancementCost(skill.EnhancementLevel) : null,
                        TotalPercent = definition is null ? 0 : weaponCatalog.CalculateSkillPercent(definition, skill.Level),
                        Description = definition is null ? "未知技能" : weaponCatalog.DescribeSkill(definition, skill.Level),
                        IsActive = definition is not null && item.EquippedSlotIndex.HasValue && item.Element == main?.Element
                    };
                }).ToList()
            }).ToList()
        };
    }

    private static int ParseFragmentTier(string code) =>
        int.TryParse(code["weapon-fragment-t".Length..], out var tier) && tier > 0 ? tier : 1;
}
