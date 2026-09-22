using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class WeaponService(GameDbContext dbContext, UserService userService, SkillCatalog skillCatalog, WeaponCatalog weaponCatalog)
{
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
        catch (DbUpdateException)
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
        catch (DbUpdateException)
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
        if (weapons.Any(weapon => !WeaponCatalog.CanSell(weapon))) return (null, "StarterWeaponCannotBeSold");
        var user = await dbContext.Users.SingleAsync(item => item.Id == character!.UserId);
        var gold = weapons.Sum(weapon => weapon.SellGold);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            user.Gold = checked(user.Gold + gold);
            user.Version++;
            character!.Version++;
            dbContext.CharacterWeapons.RemoveRange(weapons);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException)
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
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> EnhanceSkillAsync(
        string? token, int characterId, int weaponId, int skillSlotIndex)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (await GetArmoryLockErrorAsync(characterId) is { } lockError) return (null, lockError);
        var weapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
            .Where(weapon => weapon.CharacterId == characterId).ToListAsync();
        var weapon = weapons.SingleOrDefault(item => item.Id == weaponId);
        if (weapon is null) return (null, "WeaponNotOwned");
        var skill = weapon.Skills.SingleOrDefault(item => item.SlotIndex == skillSlotIndex);
        if (skill is null) return (null, "WeaponSkillNotFound");
        if (skill.EnhancementLevel >= WeaponRules.MaxEnhancementPerSkill) return (null, "WeaponSkillAtMaximum");
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
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character!), null);
        }
        catch (DbUpdateException)
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

    private async Task<string?> GetArmoryLockErrorAsync(int characterId)
    {
        var room = await (from slot in dbContext.RoomSlots
            join candidate in dbContext.Rooms on slot.RoomId equals candidate.Id
            where slot.CharacterId == characterId
            select candidate).SingleOrDefaultAsync();
        return room is not null && room.Status != RoomStatus.BattleOver &&
               (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0)
            ? "LoadoutLocked" : null;
    }

    private void RecalculateCharacter(Character character, IReadOnlyCollection<CharacterWeapon> weapons)
    {
        var equipped = weapons.Where(weapon => weapon.EquippedSlotIndex.HasValue).ToList();
        character.Attack = equipped.Sum(weapon => weapon.Attack);
        character.MaxHp = equipped.Sum(weapon => weapon.MaxHp);
        weaponCatalog.ApplyBonuses(character, weapons);
        character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
        character.Version++;
    }

    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var character = await dbContext.Characters.SingleOrDefaultAsync(item => item.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user!.Id ? (character, null) : (null, "NotOwner");
    }

    private async Task<CharacterWeaponsResponse> BuildResponseAsync(Character character)
    {
        var weapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
            .Where(item => item.CharacterId == character.Id)
            .OrderBy(item => item.EquippedSlotIndex == null).ThenBy(item => item.EquippedSlotIndex)
            .ThenByDescending(item => item.ItemLevel).ThenBy(item => item.Id).ToListAsync();
        var fragmentStacks = await dbContext.CharacterItemStacks.Where(stack =>
            stack.CharacterId == character.Id && stack.ItemCode.StartsWith("weapon-fragment-t")).ToListAsync();
        var main = weapons.SingleOrDefault(item => item.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        var bonuses = weaponCatalog.CalculateBonuses(weapons);
        var userGold = await dbContext.Users.Where(user => user.Id == character.UserId).Select(user => user.Gold).SingleAsync();
        var highestStackTier = fragmentStacks.Select(stack => ParseFragmentTier(stack.ItemCode)).DefaultIfEmpty(1).Max();
        var maximumTier = Math.Max(weaponCatalog.MaxFragmentTier, highestStackTier);
        return new CharacterWeaponsResponse
        {
            CharacterId = character.Id,
            CharacterName = character.Name,
            ProfessionName = skillCatalog.FindProfession(character.ProfessionCode)?.Name ?? character.ProfessionCode,
            Gold = userGold,
            Hp = character.Hp,
            TotalAttack = character.Attack,
            TotalMaxHp = character.MaxHp,
            EffectiveAttack = TalentRules.EffectiveAttack(character),
            EffectiveMaxHp = TalentRules.EffectiveMaxHp(character),
            Defense = TalentRules.EffectiveDefense(character),
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
                SellGold = WeaponCatalog.CanSell(item) ? item.SellGold : 0,
                CanSell = WeaponCatalog.CanSell(item),
                Origin = item.Origin,
                DismantleFragments = WeaponCatalog.BaseDismantleReturn(item),
                DismantleReturnQuantity = weaponCatalog.DismantleReturn(item),
                CanDismantle = weaponCatalog.CanDismantle(item),
                QualityBonusLevel = Math.Clamp(item.Skills.Sum(skill => skill.QualityBonusLevel),
                    0, WeaponRules.MaxQualityBonusLevels),
                QualityName = WeaponRules.QualityName(item.Skills.Sum(skill => skill.QualityBonusLevel)),
                QualityCode = WeaponRules.QualityCode(item.Skills.Sum(skill => skill.QualityBonusLevel)),
                IsLocked = item.IsLocked,
                EquippedSlotIndex = item.EquippedSlotIndex,
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
                        QualityBonusLevel = skill.QualityBonusLevel,
                        EnhancementLevel = skill.EnhancementLevel,
                        MaximumEnhancementLevel = WeaponRules.MaxEnhancementPerSkill,
                        NextEnhancementCost = skill.EnhancementLevel < WeaponRules.MaxEnhancementPerSkill
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
