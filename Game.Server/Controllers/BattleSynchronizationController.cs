using Game.Server.Services;
using Game.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/battle/snapshot")]
public sealed class BattleSynchronizationController(BattleSynchronizationService synchronization) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Synchronize([FromBody] BattleSyncRequest request)
    {
        var authorization = Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        var (response, error) = await synchronization.SynchronizeAsync(request, token);
        return response is not null ? Ok(response) : error switch
        {
            "Unauthorized" => Unauthorized(),
            "NotFound" or "UserNotFound" or "MonsterNotFound" => NotFound(),
            "ConcurrencyConflict" => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
