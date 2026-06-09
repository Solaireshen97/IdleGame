using Game.Server.Services;
using Game.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/battle")]
public class BattleController(BattleService battleService) : ControllerBase
{
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] BattleRequest request)
    {
        var result = await battleService.ExecuteBattleAsync(request.RoomId);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(result);
    }
}
