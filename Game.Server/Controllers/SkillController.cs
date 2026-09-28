using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/skills")]
public sealed class SkillController(SkillService skillService, CombatProfessionService professions) : ControllerBase
{
    [HttpGet("professions")]
    public IActionResult GetProfessions() => Ok(skillService.GetProfessions());

    [HttpGet("professions/{characterId:int}")]
    public async Task<IActionResult> GetCharacterProfessions(int characterId)
    {
        var (response, error) = await professions.GetAsync(GetToken(), characterId);
        return error is null ? Ok(response) : Error(error);
    }

    [HttpPost("professions/{characterId:int}/switch")]
    public async Task<IActionResult> SwitchProfession(int characterId, SwitchCombatProfessionRequest request)
    {
        var (response, error) = await professions.SwitchAsync(GetToken(), characterId, request);
        return error is null ? Ok(response) : Error(error);
    }

    private IActionResult Error(string error) => error switch
    {
        "Unauthorized" => Unauthorized(error),
        "CharacterNotFound" => NotFound(error),
        "NotOwner" => StatusCode(StatusCodes.Status403Forbidden, error),
        "CharacterBusy" => Conflict(error),
        _ => BadRequest(error)
    };

    private string? GetToken()
    {
        var value = Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }
}
