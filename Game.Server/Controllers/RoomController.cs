using Game.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/rooms")]
public class RoomController(RoomService roomService) : ControllerBase
{
    [HttpGet("{roomId:int}")]
    public async Task<IActionResult> GetRoom(int roomId)
    {
        var roomState = await roomService.GetRoomStateAsync(roomId);
        if (roomState is null)
        {
            return NotFound();
        }

        return Ok(roomState);
    }
}
