using System.Security.Cryptography;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class UserService(GameDbContext dbContext, ProgressionService progressionService, SkillCatalog skillCatalog, WeaponCatalog? weaponCatalog = null)
{
    private static readonly PasswordHasher<User> PasswordHasher = new();
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public async Task<(AuthResponse? Response, string? Error)> RegisterAsync(RegisterRequest request)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userName))
        {
            return (null, "UserNameRequired");
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return (null, "PasswordRequired");
        }

        var existingUser = await dbContext.Users.FirstOrDefaultAsync(x => x.UserName == userName);
        if (existingUser is not null)
        {
            return (null, "DuplicateUserName");
        }

        var user = new User
        {
            UserName = userName,
            Gold = weaponCatalog?.StartingAccountGold ?? 0
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, request.Password);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var character = CreateCharacterEntity(user.Id, "Knight", SkillRules.DefaultProfessionCode);
        dbContext.Characters.Add(character);

        var session = CreateSession(user.Id);
        dbContext.UserLoginSessions.Add(session);

        await dbContext.SaveChangesAsync();
        AddStartingSkills(character);
        AddStartingWeapons(character);
        user.ActiveCharacterId = character.Id;
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (BuildAuthResponse(user, session.Token), null);
    }

    public async Task<(AuthResponse? Response, string? Error)> LoginAsync(LoginRequest request)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userName))
        {
            return (null, "UserNameRequired");
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return (null, "PasswordRequired");
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.UserName == userName);
        if (user is null)
        {
            return (null, "InvalidCredentials");
        }

        var verifyResult = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verifyResult == PasswordVerificationResult.Failed)
        {
            return (null, "InvalidCredentials");
        }

        var session = CreateSession(user.Id);
        dbContext.UserLoginSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return (BuildAuthResponse(user, session.Token), null);
    }

    public async Task<(CurrentUserResponse? Response, string? Error)> GetCurrentUserAsync(string? token)
    {
        var user = await GetUserByTokenAsync(token);
        if (user is null)
        {
            return (null, "Unauthorized");
        }

        return (new CurrentUserResponse
        {
            UserId = user.Id,
            UserName = user.UserName,
            Gold = user.Gold
        }, null);
    }

    public async Task<(CurrentCharacterResponse? Response, string? Error)> GetCurrentCharacterAsync(string? token)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null)
        {
            return (null, error);
        }

        var character = await ResolveActiveCharacterAsync(user!);
        if (character is null)
        {
            return (null, "CharacterNotFound");
        }

        return (BuildCurrentCharacterResponse(character), null);
    }

    public async Task<(List<CharacterSummaryResponse>? Response, string? Error)> GetCurrentCharactersAsync(string? token)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null)
        {
            return (null, error);
        }

        var characters = await dbContext.Characters
            .Where(x => x.UserId == user!.Id)
            .OrderBy(x => x.Id)
            .ToListAsync();

        var currentCharacter = await ResolveActiveCharacterAsync(user!, characters);
        var response = characters
            .Select(x => BuildCharacterSummary(x, currentCharacter?.Id == x.Id))
            .ToList();

        return (response, null);
    }

    public async Task<(CharacterSummaryResponse? Response, string? Error)> SelectCurrentCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null)
        {
            return (null, error);
        }

        var targetCharacter = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == characterId);
        if (targetCharacter is null)
        {
            return (null, "CharacterNotFound");
        }

        if (targetCharacter.UserId != user!.Id)
        {
            return (null, "NotOwner");
        }

        var currentCharacter = await ResolveActiveCharacterAsync(user);
        if (currentCharacter?.Id != targetCharacter.Id)
        {
            user.ActiveCharacterId = targetCharacter.Id;
            await dbContext.SaveChangesAsync();
        }

        return (BuildCharacterSummary(targetCharacter, isCurrent: true), null);
    }

    public async Task<(User? User, string? Error)> GetCurrentUserEntityAsync(string? token)
    {
        var session = await GetValidSessionAsync(token);
        if (session is null)
        {
            return (null, "Unauthorized");
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == session.UserId);
        if (user is null)
        {
            return (null, "UserNotFound");
        }

        return (user, null);
    }

    public async Task<(User? User, Character? Character, string? Error)> GetCurrentUserAndActiveCharacterAsync(string? token)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null)
        {
            return (null, null, error);
        }

        var character = await ResolveActiveCharacterAsync(user!);
        if (character is null)
        {
            return (user, null, "CharacterNotFound");
        }

        return (user, character, null);
    }

    public async Task<(CharacterSummaryResponse? Response, string? Error)> CreateCurrentCharacterAsync(string? token, CreateCharacterRequest request)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null)
        {
            return (null, error);
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "InvalidName");
        }

        var professionCode = request.ProfessionCode?.Trim() ?? string.Empty;
        if (skillCatalog.FindProfession(professionCode) is null) return (null, "InvalidProfession");

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var character = CreateCharacterEntity(user!.Id, name, professionCode);
        dbContext.Characters.Add(character);
        await dbContext.SaveChangesAsync();
        AddStartingSkills(character);
        AddStartingWeapons(character);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (BuildCharacterSummary(character), null);
    }

    public async Task<(bool Success, string? Error)> DeleteCurrentCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null)
        {
            return (false, error);
        }

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

        var isCharacterInRoom = await dbContext.RoomSlots.AnyAsync(x => x.CharacterId == characterId);
        if (isCharacterInRoom)
        {
            return (false, "CharacterInRoom");
        }

        await ResolveActiveCharacterAsync(user);
        if (user.ActiveCharacterId == characterId)
        {
            var nextCharacter = await dbContext.Characters
                .Where(x => x.UserId == user.Id && x.Id != characterId)
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync();
            user.ActiveCharacterId = nextCharacter?.Id;
        }

        dbContext.CharacterItemStacks.RemoveRange(await dbContext.CharacterItemStacks.Where(item => item.CharacterId == characterId).ToListAsync());
        dbContext.CharacterConsumableSlots.RemoveRange(await dbContext.CharacterConsumableSlots.Where(slot => slot.CharacterId == characterId).ToListAsync());
        dbContext.BattleConsumableCooldowns.RemoveRange(await dbContext.BattleConsumableCooldowns.Where(cooldown => cooldown.CharacterId == characterId).ToListAsync());
        dbContext.CharacterSkillSlots.RemoveRange(await dbContext.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync());
        dbContext.CharacterSkillTalents.RemoveRange(await dbContext.CharacterSkillTalents.Where(talent => talent.CharacterId == characterId).ToListAsync());
        dbContext.BattleSkillCooldowns.RemoveRange(await dbContext.BattleSkillCooldowns.Where(cooldown => cooldown.CharacterId == characterId).ToListAsync());
        dbContext.CharacterWeapons.RemoveRange(await dbContext.CharacterWeapons.Where(weapon => weapon.CharacterId == characterId).ToListAsync());
        dbContext.Characters.Remove(character);
        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> LogoutAsync(string? token)
    {
        var session = await GetValidSessionAsync(token);
        if (session is null)
        {
            return (false, "Unauthorized");
        }

        dbContext.UserLoginSessions.Remove(session);
        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    private async Task<User?> GetUserByTokenAsync(string? token)
    {
        var session = await GetValidSessionAsync(token);
        if (session is null)
        {
            return null;
        }

        return await dbContext.Users.FirstOrDefaultAsync(x => x.Id == session.UserId);
    }

    private async Task<UserLoginSession?> GetValidSessionAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var session = await dbContext.UserLoginSessions.FirstOrDefaultAsync(x => x.Token == token);
        if (session is null)
        {
            return null;
        }

        if (session.ExpireAt <= DateTime.UtcNow)
        {
            dbContext.UserLoginSessions.Remove(session);
            await dbContext.SaveChangesAsync();
            return null;
        }

        return session;
    }

    private static UserLoginSession CreateSession(int userId)
    {
        var now = DateTime.UtcNow;
        return new UserLoginSession
        {
            UserId = userId,
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = now,
            ExpireAt = now.Add(SessionLifetime)
        };
    }

    private static AuthResponse BuildAuthResponse(User user, string token)
    {
        return new AuthResponse
        {
            Token = token,
            UserId = user.Id,
            UserName = user.UserName
        };
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
            Attack = 0,
            Defense = 5
        };
    }

    private void AddStartingSkills(Character character)
    {
        var profession = skillCatalog.FindProfession(character.ProfessionCode)!;
        for (var index = 0; index < profession.StartingSkills.Count; index++)
            dbContext.CharacterSkillSlots.Add(new CharacterSkillSlot
            {
                CharacterId = character.Id,
                SlotIndex = index + 1,
                SkillCode = profession.StartingSkills[index],
                AutoHpThresholdPercent = SkillRules.DefaultAutoHpThresholdPercent
            });
    }

    private void AddStartingWeapons(Character character)
    {
        var catalog = weaponCatalog ?? throw new InvalidOperationException("A weapon catalog is required to create characters.");
        var weapons = catalog.CreateStarterWeapons(character.Id, character.ProfessionCode);
        dbContext.CharacterWeapons.AddRange(weapons);
        var main = weapons.Single(weapon => weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        character.Attack = main.Attack;
        character.MaxHp = main.MaxHp;
        catalog.ApplyBonuses(character, weapons);
        character.Hp = TalentRules.EffectiveMaxHp(character);
        character.Version++;
    }

    private async Task<Character?> ResolveActiveCharacterAsync(User user, List<Character>? characters = null)
    {
        characters ??= await dbContext.Characters
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.Id)
            .ToListAsync();

        if (characters.Count == 0)
        {
            if (user.ActiveCharacterId is not null)
            {
                user.ActiveCharacterId = null;
                await dbContext.SaveChangesAsync();
            }

            return null;
        }

        var activeCharacter = user.ActiveCharacterId.HasValue
            ? characters.FirstOrDefault(x => x.Id == user.ActiveCharacterId.Value)
            : null;
        var resolvedCharacter = activeCharacter ?? characters[0];

        if (user.ActiveCharacterId != resolvedCharacter.Id)
        {
            user.ActiveCharacterId = resolvedCharacter.Id;
            await dbContext.SaveChangesAsync();
        }

        return resolvedCharacter;
    }

    private CurrentCharacterResponse BuildCurrentCharacterResponse(Character character)
    {
        return new CurrentCharacterResponse
        {
            CharacterId = character.Id,
            Name = character.Name,
            ProfessionCode = character.ProfessionCode,
            ProfessionName = skillCatalog.FindProfession(character.ProfessionCode)?.Name ?? character.ProfessionCode,
            Hp = character.Hp,
            MaxHp = TalentRules.EffectiveMaxHp(character),
            Attack = TalentRules.EffectiveAttack(character),
            Defense = TalentRules.EffectiveDefense(character),
            Level = character.Level,
            Experience = character.Experience,
            ExperienceToNextLevel = progressionService.GetExperienceToNextLevel(character.Level),
            TalentPoints = character.TalentPoints
        };
    }

    private CharacterSummaryResponse BuildCharacterSummary(Character character, bool isCurrent = false)
    {
        return new CharacterSummaryResponse
        {
            CharacterId = character.Id,
            Name = character.Name,
            ProfessionCode = character.ProfessionCode,
            ProfessionName = skillCatalog.FindProfession(character.ProfessionCode)?.Name ?? character.ProfessionCode,
            Hp = character.Hp,
            MaxHp = TalentRules.EffectiveMaxHp(character),
            Attack = TalentRules.EffectiveAttack(character),
            Defense = TalentRules.EffectiveDefense(character),
            Level = character.Level,
            Experience = character.Experience,
            ExperienceToNextLevel = progressionService.GetExperienceToNextLevel(character.Level),
            TalentPoints = character.TalentPoints,
            IsCurrent = isCurrent
        };
    }
}
