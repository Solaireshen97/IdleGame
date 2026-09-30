using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Owns account selection, character initialization and character-owned data deletion.</summary>
public sealed class CharacterLifecycleService(GameDbContext dbContext, SkillCatalog skillCatalog, WeaponCatalog? weaponCatalog)
{
    public async Task<(Character? Character, string? Error)> SelectAsync(User user, int characterId)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var (character, error) = await new CharacterAccessResolver(dbContext).OwnedAsync(user, characterId);
        if (error is not null) return (null, error);
        user.ActiveCharacterId = characterId;
        user.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (character, null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(Character? Character, string? Error)> CreateAsync(User user, CreateCharacterRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "InvalidName");
        }

        var professionCode = request.ProfessionCode?.Trim() ?? string.Empty;
        if (skillCatalog.FindProfession(professionCode) is not { IsPromotion: false }) return (null, "InvalidProfession");

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var characterCount = await dbContext.Characters.CountAsync(character => character.UserId == user!.Id);
        if (characterCount >= user!.CharacterSlotLimit) return (null, "CharacterSlotLimitReached");

        var character = CreateCharacterEntity(user!.Id, name, professionCode);
        if (characterCount == 0) character.Gold = weaponCatalog?.StartingCharacterGold ?? 0;
        dbContext.Characters.Add(character);
        user.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            AddStartingSkills(character);
            dbContext.CharacterCombatProfessions.Add(new CharacterCombatProfession
            {
                CharacterId = character.Id, ProfessionCode = character.ProfessionCode,
                Level = character.Level, Experience = character.Experience
            });
            AddStartingWeapons(character);
            if (characterCount == 0) user.ActiveCharacterId = character.Id;
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }

        return (character, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(User user, int characterId)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == characterId);
        if (character is null)
        {
            return (false, "CharacterNotFound");
        }

        if (character.UserId != user!.Id)
        {
            return (false, "NotOwner");
        }

        var characterCount = await dbContext.Characters.CountAsync(x => x.UserId == user.Id);
        if (characterCount <= 1)
        {
            return (false, "CannotDeleteLastCharacter");
        }

        if (await dbContext.RoomSlots.AnyAsync(x => x.CharacterId == characterId))
            return (false, "CharacterInRoom");
        if (await dbContext.CharacterActivities.AnyAsync(activity => activity.CharacterId == characterId))
            return (false, "CharacterBusy");

        // Create, select and delete share the account version, including requests
        // deleting different characters. Saving must preserve the account invariant.
        user.Version++;
        if (user.ActiveCharacterId == characterId || !await dbContext.Characters.AnyAsync(
                item => item.UserId == user.Id && item.Id == user.ActiveCharacterId))
        {
            user.ActiveCharacterId = await dbContext.Characters
                .Where(item => item.UserId == user.Id && item.Id != characterId)
                .OrderBy(item => item.Id).Select(item => (int?)item.Id).FirstOrDefaultAsync();
        }

        dbContext.CharacterItemStacks.RemoveRange(await dbContext.CharacterItemStacks.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.CharacterBattleMilestones.RemoveRange(await dbContext.CharacterBattleMilestones.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.CharacterDungeonProgress.RemoveRange(await dbContext.CharacterDungeonProgress.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.DungeonRunParticipants.RemoveRange(await dbContext.DungeonRunParticipants.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.CharacterGatheringOpportunities.RemoveRange(await dbContext.CharacterGatheringOpportunities.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.CharacterProfessionTalents.RemoveRange(await dbContext.CharacterProfessionTalents.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.GatheringTasks.RemoveRange(await dbContext.GatheringTasks.Where(task => task.CharacterId == characterId).ToListAsync());
        dbContext.ProductionTasks.RemoveRange(await dbContext.ProductionTasks.Where(task => task.CharacterId == characterId).ToListAsync());
        dbContext.CharacterGardenPlots.RemoveRange(await dbContext.CharacterGardenPlots.Where(plot => plot.CharacterId == characterId).ToListAsync());
        dbContext.LogisticsRequests.RemoveRange(await dbContext.LogisticsRequests.Where(request => request.CharacterId == characterId).ToListAsync());
        dbContext.CharacterConsumableSlots.RemoveRange(await dbContext.CharacterConsumableSlots.Where(slot => slot.CharacterId == characterId).ToListAsync());
        dbContext.BattleConsumableCooldowns.RemoveRange(await dbContext.BattleConsumableCooldowns.Where(cooldown => cooldown.CharacterId == characterId).ToListAsync());
        dbContext.BattleOperationPotionStates.RemoveRange(await dbContext.BattleOperationPotionStates.Where(state => state.CharacterId == characterId).ToListAsync());
        dbContext.BattleHealingPotionStates.RemoveRange(await dbContext.BattleHealingPotionStates.Where(state => state.CharacterId == characterId).ToListAsync());
        dbContext.CharacterSkillSlots.RemoveRange(await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync());
        dbContext.CharacterCombatProfessions.RemoveRange(await dbContext.CharacterCombatProfessions.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.CharacterSkillTalents.RemoveRange(await dbContext.CharacterSkillTalents.Where(talent => talent.CharacterId == characterId).ToListAsync());
        dbContext.BattleSkillCooldowns.RemoveRange(await dbContext.BattleSkillCooldowns.Where(cooldown => cooldown.CharacterId == characterId).ToListAsync());
        dbContext.CharacterSoulImprints.RemoveRange(await dbContext.CharacterSoulImprints.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.CharacterWeapons.RemoveRange(await dbContext.CharacterWeapons.Where(weapon => weapon.CharacterId == characterId).ToListAsync());
        dbContext.CharacterFirstHuntWeaponClaims.RemoveRange(await dbContext.CharacterFirstHuntWeaponClaims.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.BattleConsumableBuffs.RemoveRange(await dbContext.BattleConsumableBuffs.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.Characters.Remove(character);
        try
        {
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            return (false, "ConcurrencyConflict");
        }
    }

    private static Character CreateCharacterEntity(int userId, string name, string professionCode)
    {
        return new Character
        {
            UserId = userId,
            Name = name,
            ProfessionCode = professionCode,
            Hp = 0,
            MaxHp = 0,
            Attack = 0
        };
    }

    private void AddStartingSkills(Character character)
    {
        var available = skillCatalog.SkillsAtLevel(character.ProfessionCode, character.Level);
        for (var index = 0; index < available.Count; index++)
            dbContext.CharacterSkillSlots.Add(new CharacterSkillSlot
            {
                CharacterId = character.Id,
                SlotIndex = index + 1,
                SkillCode = available[index].Code,
                AutoHpThresholdPercent = SkillRules.DefaultAutoHpThresholdPercent
            });
    }

    private void AddStartingWeapons(Character character)
    {
        var catalog = weaponCatalog ?? throw new InvalidOperationException("A weapon catalog is required to create characters.");
        var weapons = catalog.CreateStarterWeapons(character.Id, character.ProfessionCode);
        dbContext.CharacterWeapons.AddRange(weapons);
        catalog.RecalculateEquipmentStats(character, weapons);
        character.Hp = TalentRules.EffectiveMaxHp(character);
        character.Version++;
    }

}
