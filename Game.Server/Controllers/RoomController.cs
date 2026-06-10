using Game.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/rooms")]
public class RoomController(RoomService roomService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetRooms()
    {
        var rooms = await roomService.GetRoomsAsync();
        return Ok(rooms);
    }

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

    [HttpPost]
    public async Task<IActionResult> CreateRoom()
    {
        var roomState = await roomService.CreateRoomAsync();
        if (roomState is null)
        {
            return BadRequest("Failed to create room. Default player or character not found.");
        }

        return Ok(roomState);
    }

    [HttpDelete("{roomId:int}")]
    public async Task<IActionResult> DeleteRoom(int roomId)
    {
        var (success, error) = await roomService.DeleteRoomAsync(roomId);

        if (!success)
        {
            if (error == "NotFound")
            {
                return NotFound();
            }

            return BadRequest(error);
        }

        return NoContent();
    }
}
