using Game.Server.Services;
using Game.Shared.Dtos.Production;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/production")]
public sealed class ProductionController(ProductionService production) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var (response, error) = await production.GetAsync(GetBearerToken());
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartProductionRequest? request)
    {
        if (request is null) return BadRequest("InvalidRequest");
        var (response, error) = await production.StartAsync(GetBearerToken(), request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("{taskId:int}/stop")]
    public async Task<IActionResult> Stop(int taskId)
    {
        var (response, error) = await production.StopAsync(GetBearerToken(), taskId);
        return error is null ? Ok(response) : ToError(error);
    }

    private IActionResult ToError(string error) => error switch
    {
        "Unauthorized" => Unauthorized(),
        "UserNotFound" or "CharacterNotFound" or "RecipeNotFound" or "TaskNotFound" => NotFound(error),
        "ActiveCharacterChanged" or "CharacterBusy" or "ConcurrencyConflict" => Conflict(error),
        "RecipeLocked" or "LevelTooLow" or "AlchemyLevelTooLow" or "InsufficientMaterials" =>
            StatusCode(StatusCodes.Status403Forbidden, error),
        _ => BadRequest(error)
    };

    private string? GetBearerToken()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim() : null;
    }
}
