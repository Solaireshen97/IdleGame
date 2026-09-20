using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class TalentService(GameDbContext dbContext, UserService userService)
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

        var rank = GetRank(character!, type);
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

        var spent = TalentRules.SpentPoints(character!.AttackTalentRank) +
                    TalentRules.SpentPoints(character.DefenseTalentRank) +
                    TalentRules.SpentPoints(character.HealthTalentRank);
        if (spent == 0) return (BuildResponse(character), null);

        character.TalentPoints += spent;
        character.AttackTalentRank = 0;
        character.DefenseTalentRank = 0;
        character.HealthTalentRank = 0;
        character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
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

    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user!.Id ? (character, null) : (null, "NotOwner");
    }

    private static int GetRank(Character character, TalentType type) => type switch
    {
        TalentType.Attack => character.AttackTalentRank,
        TalentType.Defense => character.DefenseTalentRank,
        TalentType.Health => character.HealthTalentRank,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

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
