using Game.Server.Services;
using Game.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/battle")]
public class BattleController(BattleService battleService, RoomService roomService) : ControllerBase
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

    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] BattleRequest request)
    {
        var success = await battleService.ResetBattleAsync(request.RoomId);
        if (!success)
        {
            return NotFound();
        }

        var roomState = await roomService.GetRoomStateAsync(request.RoomId);
        if (roomState is null)
        {
            return NotFound();
        }

        return Ok(roomState);
    }

    [HttpPost("heal")]
    public async Task<IActionResult> Heal([FromBody] BattleRequest request)
    {
        var success = await battleService.HealCharacterAsync(request.RoomId);
        if (!success)
        {
            return NotFound();
        }

        var roomState = await roomService.GetRoomStateAsync(request.RoomId);
        if (roomState is null)
        {
            return NotFound();
        }

        return Ok(roomState);
    }
}
