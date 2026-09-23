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

    [HttpPut("{weaponId:int}/lock")]
    public async Task<ActionResult<CharacterWeaponsResponse>> SetLock(
        int characterId, int weaponId, [FromBody] SetWeaponLockRequest request)
    {
        var (response, error) = await weaponService.SetLockAsync(GetToken(), characterId, weaponId, request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("sell")]
    public async Task<ActionResult<CharacterWeaponsResponse>> Sell(
        int characterId, [FromBody] WeaponBatchRequest request)
    {
        var (response, error) = await weaponService.SellAsync(GetToken(), characterId, request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("dismantle")]
    public async Task<ActionResult<CharacterWeaponsResponse>> Dismantle(
        int characterId, [FromBody] WeaponBatchRequest request)
    {
        var (response, error) = await weaponService.DismantleAsync(GetToken(), characterId, request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("{weaponId:int}/skills/{skillSlotIndex:int}/enhance")]
    public async Task<ActionResult<CharacterWeaponsResponse>> EnhanceSkill(
        int characterId, int weaponId, int skillSlotIndex)
    {
        var (response, error) = await weaponService.EnhanceSkillAsync(
            GetToken(), characterId, weaponId, skillSlotIndex);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("{weaponId:int}/quality/upgrade")]
    public async Task<ActionResult<CharacterWeaponsResponse>> UpgradeQuality(
        int characterId, int weaponId, [FromBody] UpgradeWeaponQualityRequest request)
    {
        var (response, error) = await weaponService.UpgradeQualityAsync(
            GetToken(), characterId, weaponId, request.MaterialWeaponId);
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
        "LoadoutLocked" or "ConcurrencyConflict" or "WeaponEquipped" or "WeaponLocked" => Conflict(error),
        _ => BadRequest(error)
    };
}
