using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class TalentService(GameDbContext dbContext, UserService userService, SkillCatalog skillCatalog)
{
    public async Task<(CharacterTalentsResponse? Response, string? Error)> GetAsync(string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        return error is null ? (BuildResponse(character!), null) : (null, error);
    }

    public async Task<(CharacterTalentsResponse? Response, string? Error)> AllocateAsync(string? token, int characterId, TalentType type)
    {
        if (!Enum.IsDefined(type)) return (null, "InvalidTalent");
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (await IsTreeLockedAsync(characterId)) return (null, "LoadoutLocked");

        var rank = TalentRules.GetRank(character!, type);
        if (rank >= TalentRules.MaxRank) return (null, "TalentMaxRank");
        var cost = TalentRules.NextRankCost(rank);
        if (character!.TalentPoints < cost) return (null, "InsufficientTalentPoints");

        SetRank(character, type, rank + 1);
        character.TalentPoints -= cost;
        character.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (BuildResponse(character), null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(CharacterTalentsResponse? Response, string? Error)> ResetAsync(string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (await IsTreeLockedAsync(characterId)) return (null, "LoadoutLocked");

        var spent = TalentRules.SpentPoints(character!.AttackTalentRank) +
                    TalentRules.SpentPoints(character.DefenseTalentRank) +
                    TalentRules.SpentPoints(character.HealthTalentRank);
        var purchased = await dbContext.CharacterSkillTalents.Where(node => node.CharacterId == characterId).ToListAsync();
        if (spent == 0 && purchased.Count == 0) return (BuildResponse(character), null);

        character.TalentPoints = checked(character.TalentPoints + spent + purchased.Sum(node => node.PointsSpent));
        character.AttackTalentRank = 0;
        character.DefenseTalentRank = 0;
        character.HealthTalentRank = 0;
        character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
        var startingSkills = skillCatalog.FindProfession(character.ProfessionCode)?.StartingSkills
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var equipped = await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync();
        foreach (var slot in equipped.Where(slot => slot.SkillCode is not null && !startingSkills.Contains(slot.SkillCode)))
        {
            slot.SkillCode = null;
            slot.AutoUseEnabled = false;
            slot.Version++;
        }
        var cooldowns = await dbContext.BattleSkillCooldowns.Where(entry => entry.CharacterId == characterId).ToListAsync();
        dbContext.BattleSkillCooldowns.RemoveRange(cooldowns.Where(entry => !startingSkills.Contains(entry.SkillCode)));
        dbContext.CharacterSkillTalents.RemoveRange(purchased);
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        if (roomSlot is not null)
        {
            roomSlot.PendingSkillSlotMask = 0;
            var room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
            if (room is not null) room.Version++;
        }
        character.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (BuildResponse(character), null);
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
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user!.Id ? (character, null) : (null, "NotOwner");
    }

    private async Task<bool> IsTreeLockedAsync(int characterId)
    {
        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        if (roomSlot is null) return false;
        var room = await dbContext.Rooms.FindAsync(roomSlot.RoomId);
        return room is not null && room.Status != RoomStatus.BattleOver &&
               (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0);
    }

    private static void SetRank(Character character, TalentType type, int rank)
    {
        switch (type)
        {
            case TalentType.Attack: character.AttackTalentRank = rank; break;
            case TalentType.Defense: character.DefenseTalentRank = rank; break;
            case TalentType.Health: character.HealthTalentRank = rank; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static CharacterTalentsResponse BuildResponse(Character character) => new()
    {
        CharacterId = character.Id,
        Name = character.Name,
        Level = character.Level,
        TalentPoints = character.TalentPoints,
        Hp = character.Hp,
        MaxHp = TalentRules.EffectiveMaxHp(character),
        Attack = TalentRules.EffectiveAttack(character),
        Defense = TalentRules.EffectiveDefense(character),
        Talents =
        [
            BuildNode(TalentType.Attack, character.AttackTalentRank, TalentRules.AttackPerRank),
            BuildNode(TalentType.Defense, character.DefenseTalentRank, TalentRules.DefensePerRank),
            BuildNode(TalentType.Health, character.HealthTalentRank, TalentRules.HealthPerRank)
        ]
    };

    private static TalentNodeResponse BuildNode(TalentType type, int rank, int bonusPerRank) => new()
    {
        Type = type,
        Rank = rank,
        MaxRank = TalentRules.MaxRank,
        BonusPerRank = bonusPerRank,
        NextRankCost = rank < TalentRules.MaxRank ? TalentRules.NextRankCost(rank) : null
    };
}
