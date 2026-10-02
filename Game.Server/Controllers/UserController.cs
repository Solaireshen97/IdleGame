using Game.Server.Services;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/user")]
public class UserController(UserService userService, ConsumableService consumableService, SkillService skillService) : ControllerBase
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
            "ConcurrencyConflict" => Conflict("ConcurrencyConflict"),
            _ => BadRequest()
        };
    }

    [HttpPut("characters/{characterId:int}/quick-skill-cast")]
    public async Task<IActionResult> SetQuickSkillCast(int characterId, [FromBody] SetQuickSkillCastRequest request)
    {
        var (success, error) = await userService.SetQuickSkillCastAsync(GetBearerToken(), characterId, request.IsEnabled);
        return success ? NoContent() : error switch
        {
            "Unauthorized" => Unauthorized(),
            "UserNotFound" or "CharacterNotFound" => NotFound(error),
            "NotOwner" => StatusCode(403, "NotOwner"),
            _ => BadRequest(error)
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
            "InvalidProfession" => BadRequest("InvalidProfession"),
            "CharacterSlotLimitReached" => Conflict("CharacterSlotLimitReached"),
            "ConcurrencyConflict" => Conflict("ConcurrencyConflict"),
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
            "CharacterBusy" => Conflict("CharacterBusy"),
            "ConcurrencyConflict" => Conflict("ConcurrencyConflict"),
            _ => BadRequest()
        };
    }

    [HttpGet("characters/{characterId:int}/consumables")]
    public async Task<IActionResult> GetConsumables(int characterId)
    {
        var (response, error) = await consumableService.GetAsync(GetBearerToken(), characterId);
        return ConsumableResult(response, error);
    }

    [HttpPut("characters/{characterId:int}/consumables/{slotIndex:int}")]
    public async Task<IActionResult> SetConsumableSlot(int characterId, int slotIndex, [FromBody] SetConsumableSlotRequest? request)
    {
        if (request is null) return BadRequest("Request body is required.");
        var (response, error) = await consumableService.SetSlotAsync(GetBearerToken(), characterId, slotIndex, request);
        return ConsumableResult(response, error);
    }

    private IActionResult ConsumableResult(CharacterConsumablesResponse? response, string? error) => error switch
    {
        null => Ok(response),
        "Unauthorized" => Unauthorized(),
        "UserNotFound" or "CharacterNotFound" => NotFound(error),
        "NotOwner" => StatusCode(403, "NotOwner"),
        "LoadoutLocked" or "ConsumableAlreadyEquipped" or "ConcurrencyConflict" => Conflict(error),
        _ => BadRequest(error)
    };

    [HttpPut("characters/{characterId:int}/consumables/{slotIndex:int}/auto")]
    public async Task<IActionResult> SetConsumableAuto(int characterId, int slotIndex, [FromBody] SetConsumableAutoRequest request)
    {
        var (response, error) = await consumableService.SetAutoAsync(GetBearerToken(), characterId, slotIndex, request);
        return ConsumableResult(response, error);
    }

    [HttpGet("characters/{characterId:int}/skills")]
    public async Task<IActionResult> GetSkills(int characterId)
    {
        var (response, error) = await skillService.GetAsync(GetBearerToken(), characterId);
        return SkillResult(response, error);
    }

    [HttpPut("characters/{characterId:int}/skills/{slotIndex:int}")]
    public async Task<IActionResult> SetSkillSlot(int characterId, int slotIndex, [FromBody] SetSkillSlotRequest? request)
    {
        if (request is null) return BadRequest("Request body is required.");
        var (response, error) = await skillService.SetSlotAsync(GetBearerToken(), characterId, slotIndex, request);
        return SkillResult(response, error);
    }

    [HttpPatch("characters/{characterId:int}/skills/{slotIndex:int}/auto")]
    public async Task<IActionResult> SetSkillAuto(int characterId, int slotIndex, [FromBody] SetSkillAutoRequest? request)
    {
        if (request is null) return BadRequest("Request body is required.");
        var (response, error) = await skillService.SetAutoAsync(GetBearerToken(), characterId, slotIndex, request);
        return SkillResult(response, error);
    }

    [HttpPost("characters/{characterId:int}/skills/swap")]
    public async Task<IActionResult> SwapSkillSlots(int characterId, [FromBody] SwapSkillSlotsRequest? request)
    {
        if (request is null) return BadRequest("Request body is required.");
        var (response, error) = await skillService.SwapSlotsAsync(GetBearerToken(), characterId, request);
        return SkillResult(response, error);
    }

    private IActionResult SkillResult(CharacterSkillsResponse? response, string? error) => error switch
    {
        null => Ok(response),
        "Unauthorized" => Unauthorized(),
        "UserNotFound" or "CharacterNotFound" => NotFound(error),
        "NotOwner" => StatusCode(403, "NotOwner"),
        "LoadoutLocked" or "SkillAlreadyEquipped" or "SharedSkillLimitReached" or "ConcurrencyConflict" => Conflict(error),
        _ => BadRequest(error)
    };

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
