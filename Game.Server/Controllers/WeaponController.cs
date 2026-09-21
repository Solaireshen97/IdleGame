using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/user/characters/{characterId:int}/weapons")]
public sealed class WeaponController(WeaponService weaponService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CharacterWeaponsResponse>> Get(int characterId)
    {
        var (response, error) = await weaponService.GetAsync(GetToken(), characterId);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPut("slots/{slotIndex:int}")]
    public async Task<ActionResult<CharacterWeaponsResponse>> SetSlot(
        int characterId, int slotIndex, [FromBody] SetWeaponSlotRequest request)
    {
        var (response, error) = await weaponService.SetSlotAsync(GetToken(), characterId, slotIndex, request);
        return error is null ? Ok(response) : ToError(error);
    }

    private string? GetToken()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim() : null;
    }

    private ActionResult ToError(string error) => error switch
    {
        "Unauthorized" => Unauthorized(error),
        "CharacterNotFound" => NotFound(error),
        "NotOwner" => StatusCode(StatusCodes.Status403Forbidden, error),
        "LoadoutLocked" or "ConcurrencyConflict" => Conflict(error),
        _ => BadRequest(error)
    };
}
