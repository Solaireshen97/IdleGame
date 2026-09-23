using Game.Server.Services;
using Game.Shared.Dtos.Warehouse;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/warehouse")]
public sealed class WarehouseController(WarehouseService warehouse) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var (response, error) = await warehouse.GetAsync(GetBearerToken());
        return error is null ? Ok(response) : Error(error);
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] WarehouseTransferRequest? request)
    {
        if (request is null) return BadRequest("InvalidRequest");
        var (response, error) = await warehouse.TransferAsync(GetBearerToken(), request);
        return error is null ? Ok(response) : Error(error);
    }

    private IActionResult Error(string error) => error switch
    {
        "Unauthorized" => Unauthorized(),
        "UserNotFound" or "CharacterNotFound" => NotFound(error),
        "ActiveCharacterChanged" or "RequestIdReused" or "ConcurrencyConflict" => Conflict(error),
        "ItemBound" => StatusCode(StatusCodes.Status403Forbidden, error),
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
