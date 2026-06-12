using Game.Server.Services;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/user")]
public class UserController(UserService userService) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var (response, error) = await userService.RegisterAsync(request);
        return error switch
        {
            null => Ok(response),
            "UserNameRequired" => BadRequest("UserName cannot be empty."),
            "PasswordRequired" => BadRequest("Password cannot be empty."),
            "DuplicateUserName" => BadRequest("UserName already exists."),
            _ => BadRequest("Registration failed.")
        };
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var (response, error) = await userService.LoginAsync(request);
        return error switch
        {
            null => Ok(response),
            "UserNameRequired" => BadRequest("UserName cannot be empty."),
            "PasswordRequired" => BadRequest("Password cannot be empty."),
            "InvalidCredentials" => Unauthorized("Invalid user name or password."),
            _ => BadRequest("Login failed.")
        };
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var (response, error) = await userService.GetCurrentUserAsync(GetBearerToken());
        if (error is not null)
        {
            return Unauthorized();
        }

        return Ok(response);
    }

    [HttpGet("character")]
    public async Task<IActionResult> Character()
    {
        var (response, error) = await userService.GetCurrentCharacterAsync(GetBearerToken());
        return error switch
        {
            null => Ok(response),
            "Unauthorized" => Unauthorized(),
            "UserNotFound" => NotFound("User not found."),
            "CharacterNotFound" => NotFound("Character not found."),
            _ => BadRequest()
        };
    }

    [HttpGet("characters")]
    public async Task<IActionResult> Characters()
    {
        var (response, error) = await userService.GetCurrentCharactersAsync(GetBearerToken());
        return error switch
        {
            null => Ok(response),
            "Unauthorized" => Unauthorized(),
            "UserNotFound" => NotFound("User not found."),
            _ => BadRequest()
        };
    }

    [HttpPost("character/select")]
    public async Task<IActionResult> SelectCharacter([FromBody] SelectCharacterRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var (response, error) = await userService.SelectCurrentCharacterAsync(GetBearerToken(), request.CharacterId);
        return error switch
        {
            null => Ok(response),
            "Unauthorized" => Unauthorized(),
            "UserNotFound" => NotFound("User not found."),
            "CharacterNotFound" => NotFound("Character not found."),
            "NotOwner" => StatusCode(403, "Character does not belong to current user."),
            _ => BadRequest()
        };
    }

    [HttpPost("characters")]
    public async Task<IActionResult> CreateCharacter([FromBody] CreateCharacterRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var (response, error) = await userService.CreateCurrentCharacterAsync(GetBearerToken(), request);
        return error switch
        {
            null => Ok(response),
            "Unauthorized" => Unauthorized(),
            "UserNotFound" => NotFound("User not found."),
            "InvalidName" => BadRequest("Character name cannot be empty."),
            _ => BadRequest()
        };
    }

    [HttpDelete("characters/{characterId:int}")]
    public async Task<IActionResult> DeleteCharacter(int characterId)
    {
        var (success, error) = await userService.DeleteCurrentCharacterAsync(GetBearerToken(), characterId);
        if (success)
        {
            return NoContent();
        }

        return error switch
        {
            "Unauthorized" => Unauthorized(),
            "UserNotFound" => NotFound("User not found."),
            "CharacterNotFound" => NotFound("Character not found."),
            "NotOwner" => StatusCode(403, "Character does not belong to current user."),
            "CannotDeleteLastCharacter" => BadRequest("Cannot delete the last character."),
            "CharacterInRoom" => BadRequest("Character is still in a room."),
            _ => BadRequest()
        };
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var (success, error) = await userService.LogoutAsync(GetBearerToken());
        if (!success)
        {
            return error == "Unauthorized" ? Unauthorized() : BadRequest();
        }

        return NoContent();
    }

    private string? GetBearerToken()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authorization[prefix.Length..].Trim();
    }
}
