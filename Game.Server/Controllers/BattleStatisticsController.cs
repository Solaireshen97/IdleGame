using Game.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/rooms/{roomId:int}/statistics")]
public sealed class BattleStatisticsController(BattleStatisticsQuery query) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int roomId, string scope = "current", int? runSequence = null,
        int? monsterId = null, int? characterId = null)
    {
        var authorization = Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        var (response, error) = await query.ReadAsync(roomId, token, scope, runSequence, monsterId, characterId);
        return error switch { "NotFound" => NotFound(), null => Ok(response), _ => BadRequest(error) };
    }
}
