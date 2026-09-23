using Game.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/professions")]
public sealed class ProfessionsController(ProfessionService professions) : ControllerBase
{
    [HttpGet("{professionCode}")]
    public async Task<IActionResult> Get(string professionCode)
    {
        var (progress, error) = await professions.GetAsync(Token(), professionCode);
        return error is null ? Ok(progress) : ToError(error);
    }

    [HttpPost("{professionCode}/talents/{nodeCode}")]
    public async Task<IActionResult> Spend(string professionCode, string nodeCode)
    {
        var (progress, error) = await professions.SpendAsync(Token(), professionCode, nodeCode);
        return error is null ? Ok(progress) : ToError(error);
    }

    [HttpPost("{professionCode}/talents/reset")]
    public async Task<IActionResult> Reset(string professionCode)
    {
        var (progress, error) = await professions.ResetAsync(Token(), professionCode);
        return error is null ? Ok(progress) : ToError(error);
    }

    private IActionResult ToError(string error) => error switch
    {
        "Unauthorized" => Unauthorized(),
        "UserNotFound" or "CharacterNotFound" or "ProfessionNotFound" or "TalentNotFound" => NotFound(error),
        "ConcurrencyConflict" => Conflict(error),
        "ProfessionLevelTooLow" or "TalentPointsExhausted" or "TalentAtMaximum" or "TalentPrerequisiteMissing" or "ProfessionTalentLocked" =>
            StatusCode(StatusCodes.Status403Forbidden, error),
        _ => BadRequest(error)
    };

    private string? Token()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim() : null;
    }
}
