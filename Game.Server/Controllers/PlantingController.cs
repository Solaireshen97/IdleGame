using Game.Server.Services;
using Game.Shared.Dtos.Planting;
using Microsoft.AspNetCore.Mvc;
namespace Game.Server.Controllers;
[ApiController, Route("api/planting")]
public sealed class PlantingController(PlantingService planting) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> Get() { var (value, error) = await planting.GetAsync(Token()); return Result(value, error); }
    [HttpPost("plant")] public async Task<IActionResult> Plant(PlantGardenRequest request) { var (value, error) = await planting.PlantAsync(Token(), request); return Result(value, error); }
    [HttpPost("harvest")] public async Task<IActionResult> Harvest(HarvestGardenRequest request) { var (value, error) = await planting.HarvestAsync(Token(), request); return Result(value, error); }
    private IActionResult Result(PlantingOverviewResponse? value, string? error) => error switch
    {
        null => Ok(value), "Unauthorized" => Unauthorized(),
        "UserNotFound" or "CharacterNotFound" or "PlantNotFound" => NotFound(error),
        "ActiveCharacterChanged" or "ConcurrencyConflict" or "StalePlot" or "RequestIdConflict" => Conflict(error),
        "PlantLocked" => StatusCode(403, error), _ => BadRequest(error)
    };
    private string? Token() { var value = Request.Headers.Authorization.ToString(); return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null; }
}
