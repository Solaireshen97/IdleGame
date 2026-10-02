using System.Security.Cryptography;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Game.Server.Services;

public class UserService(GameDbContext dbContext, ProgressionService progressionService, SkillCatalog skillCatalog,
    WeaponCatalog? weaponCatalog = null,
    RoomProjectionRevision? projectionRevision = null, StoryQuestCatalog? storyCatalog = null)
{
    private readonly CharacterAccessResolver _characters = new(dbContext);
    private readonly CharacterLifecycleService _lifecycle = new(dbContext, skillCatalog, weaponCatalog, storyCatalog);
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
            CharacterSlotLimit = CharacterSlotRules.SlotsPerAccount
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, request.Password);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        try
        {
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            var session = CreateSession(user.Id);
            dbContext.UserLoginSessions.Add(session);
            dbContext.UserStoryStates.Add(new UserStoryState { UserId = user.Id });

            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return (BuildAuthResponse(user, session.Token), null);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException
            { SqliteExtendedErrorCode: 2067 } sqlite &&
            sqlite.Message.Contains("Users.UserName", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            return (null, "DuplicateUserName");
        }
        catch
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            throw;
        }
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

        var characterCount = await dbContext.Characters.CountAsync(character => character.UserId == user.Id);
        var activeCharacter = await _characters.ActiveAsync(user);
        return (new CurrentUserResponse
        {
            UserId = user.Id,
            UserName = user.UserName,
            ActiveCharacterId = activeCharacter?.Id,
            Gold = activeCharacter?.Gold ?? 0,
            CharacterCount = characterCount,
            CharacterSlotLimit = CharacterSlotRules.SlotsPerAccount,
            MaximumCharacterSlots = CharacterSlotRules.SlotsPerAccount,
            NextCharacterSlotCost = null
        }, null);
    }

    public async Task<(CurrentCharacterResponse? Response, string? Error)> GetCurrentCharacterAsync(string? token)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null)
        {
            return (null, error);
        }

        var character = await _characters.ActiveAsync(user!);
        if (character is null)
        {
            return (null, "CharacterNotFound");
        }

        var response = BuildCurrentCharacterResponse(character);
        response.ActiveBattleRoomId = await (
            from slot in dbContext.RoomSlots
            join room in dbContext.Rooms on slot.RoomId equals room.Id
            where slot.CharacterId == character.Id && room.ClosedAtUtc == null
            select (int?)room.Id).SingleOrDefaultAsync();
        return (response, null);
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

        var currentCharacter = CharacterAccessResolver.Active(user!, characters);
        var response = characters
            .Select(x => BuildCharacterSummary(x, currentCharacter?.Id == x.Id))
            .ToList();

        return (response, null);
    }

    public async Task<(CharacterSummaryResponse? Response, string? Error)> SelectCurrentCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var result = await _lifecycle.SelectAsync(user!, characterId);
        return result.Error is null ? (BuildCharacterSummary(result.Character!, true), null) : (null, result.Error);
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

        var character = await _characters.ActiveAsync(user!);
        if (character is null)
        {
            return (user, null, "CharacterNotFound");
        }

        return (user, character, null);
    }

    public async Task<(bool Success, string? Error)> SetQuickSkillCastAsync(string? token, int characterId, bool isEnabled)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null) return (false, error);

        // This preference is independent of combat. Update only this column so an
        // in-flight round can still save HP and rewards without losing the choice.
        var updated = await dbContext.Characters.Where(character => character.Id == characterId && character.UserId == user!.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(character => character.IsQuickSkillCastEnabled, isEnabled));
        if (updated == 0)
            return (false, await dbContext.Characters.AnyAsync(character => character.Id == characterId) ? "NotOwner" : "CharacterNotFound");
        projectionRevision?.Changed();
        var tracked = dbContext.Characters.Local.FirstOrDefault(character => character.Id == characterId);
        if (tracked is not null)
        {
            var property = dbContext.Entry(tracked).Property(character => character.IsQuickSkillCastEnabled);
            property.CurrentValue = property.OriginalValue = isEnabled;
            property.IsModified = false;
        }
        return (true, null);
    }

    public async Task<(CharacterSummaryResponse? Response, string? Error)> CreateCurrentCharacterAsync(string? token, CreateCharacterRequest request)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var result = await _lifecycle.CreateAsync(user!, request);
        return result.Error is null
            ? (BuildCharacterSummary(result.Character!, user!.ActiveCharacterId == result.Character!.Id), null)
            : (null, result.Error);
    }

    public async Task<(bool Success, string? Error)> DeleteCurrentCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await GetCurrentUserEntityAsync(token);
        return error is null ? await _lifecycle.DeleteAsync(user!, characterId) : (false, error);
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

    private CurrentCharacterResponse BuildCurrentCharacterResponse(Character character)
    {
        return new CurrentCharacterResponse
        {
            CharacterId = character.Id,
            Name = character.Name,
            ProfessionCode = character.ProfessionCode,
            ProfessionName = skillCatalog.EffectiveProfession(character)?.Name ?? character.ProfessionCode,
            Hp = character.Hp,
            MaxHp = TalentRules.EffectiveMaxHp(character),
            Attack = TalentRules.EffectiveAttack(character),
            Level = character.Level,
            Experience = character.Experience,
            ExperienceToNextLevel = progressionService.GetExperienceToNextLevel(character.Level)
        };
    }

    private CharacterSummaryResponse BuildCharacterSummary(Character character, bool isCurrent = false)
    {
        return new CharacterSummaryResponse
        {
            CharacterId = character.Id,
            Name = character.Name,
            ProfessionCode = character.ProfessionCode,
            ProfessionName = skillCatalog.EffectiveProfession(character)?.Name ?? character.ProfessionCode,
            Hp = character.Hp,
            MaxHp = TalentRules.EffectiveMaxHp(character),
            Attack = TalentRules.EffectiveAttack(character),
            Level = character.Level,
            Experience = character.Experience,
            ExperienceToNextLevel = progressionService.GetExperienceToNextLevel(character.Level),
            IsCurrent = isCurrent
        };
    }
}
