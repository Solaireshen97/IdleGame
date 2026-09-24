using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/user/characters/{characterId:int}/soul-imprints")]
public sealed class SoulImprintController(SoulImprintService soulImprints) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CharacterSoulImprintsResponse>> Get(int characterId)
    {
        var (response, error) = await soulImprints.GetAsync(GetToken(), characterId);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPut("equipped")]
    public async Task<ActionResult<CharacterSoulImprintsResponse>> SetEquipped(
        int characterId, [FromBody] SetSoulImprintRequest request)
    {
        var (response, error) = await soulImprints.SetEquippedAsync(GetToken(), characterId, request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPut("{soulImprintId:int}/lock")]
    public async Task<ActionResult<CharacterSoulImprintsResponse>> SetLock(
        int characterId, int soulImprintId, [FromBody] SetSoulImprintLockRequest request)
    {
        var (response, error) = await soulImprints.SetLockAsync(GetToken(), characterId, soulImprintId, request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPut("{soulImprintId:int}/auto")]
    public async Task<ActionResult<CharacterSoulImprintsResponse>> SetAuto(
        int characterId, int soulImprintId, [FromBody] SetSoulImprintAutoRequest request)
    {
        var (response, error) = await soulImprints.SetAutoAsync(GetToken(), characterId, soulImprintId, request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("dismantle")]
    public async Task<ActionResult<CharacterSoulImprintsResponse>> Dismantle(
        int characterId, [FromBody] SoulImprintBatchRequest request)
    {
        var (response, error) = await soulImprints.DismantleAsync(GetToken(), characterId, request);
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
        "CharacterNotFound" or "SoulImprintNotOwned" => NotFound(error),
        "NotOwner" => StatusCode(StatusCodes.Status403Forbidden, error),
        "LoadoutLocked" or "ConcurrencyConflict" or "SoulImprintEquipped" or "SoulImprintLocked" => Conflict(error),
        _ => BadRequest(error)
    };
}
