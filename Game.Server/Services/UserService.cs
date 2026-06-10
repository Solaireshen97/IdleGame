using System.Security.Cryptography;
using Game.Server.Data;
using Game.Shared.Dtos.Auth;
using Game.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class UserService(GameDbContext dbContext)
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
            UserName = userName
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, request.Password);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        dbContext.Characters.Add(new Character
        {
            UserId = user.Id,
            Name = "Knight",
            Hp = 100,
            MaxHp = 100,
            Attack = 20,
            Defense = 5
        });

        var session = CreateSession(user.Id);
        dbContext.UserLoginSessions.Add(session);

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
            UserName = user.UserName
        }, null);
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
}
