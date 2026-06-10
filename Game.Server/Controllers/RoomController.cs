using Game.Server.Services;
using Game.Shared.Dtos;
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
        var roomDetail = await roomService.GetRoomDetailAsync(roomId);
        if (roomDetail is null)
        {
            return NotFound();
        }

        return Ok(roomDetail);
    }

    [HttpPost]
    public async Task<IActionResult> CreateRoom([FromBody] CreateRoomRequest? request)
    {
        var monsterType = request?.MonsterType ?? "Slime";
        var roomDetail = await roomService.CreateRoomAsync(monsterType);
        if (roomDetail is null)
        {
            return BadRequest("Failed to create room.");
        }

        return Ok(roomDetail);
    }

    [HttpPost("{roomId:int}/join")]
    public async Task<IActionResult> JoinRoom(int roomId)
    {
        var (detail, error) = await roomService.JoinRoomAsync(roomId);

        if (error == "NotFound")
        {
            return NotFound();
        }

        if (error == "RoomAlreadyHasMember")
        {
            return BadRequest("Room already has a member.");
        }

        if (error is not null)
        {
            return BadRequest(error);
        }

        return Ok(detail);
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

            if (error == "NotOwner")
            {
                return StatusCode(403, "Only the room owner can dismiss the room.");
            }

            return BadRequest(error);
        }

        return NoContent();
    }
}

