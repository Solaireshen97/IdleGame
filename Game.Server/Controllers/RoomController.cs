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
        var roomDetail = await roomService.GetRoomDetailAsync(roomId, GetBearerToken());
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
        var (roomDetail, error) = await roomService.CreateRoomAsync(monsterType, GetBearerToken());
        if (roomDetail is null || error is not null)
        {
            return error switch
            {
                "Unauthorized" => Unauthorized(),
                "UserNotFound" => NotFound("User not found."),
                "CharacterNotFound" => NotFound("Character not found."),
                "UserAlreadyInRoom" => BadRequest("Player is already in a room. Please dissolve the current room first."),
                _ => BadRequest("Failed to create room.")
            };
        }

        return Ok(roomDetail);
    }

    [HttpPost("{roomId:int}/join")]
    public async Task<IActionResult> JoinRoom(int roomId)
    {
        var (detail, error) = await roomService.JoinRoomAsync(roomId, GetBearerToken());
        if (detail is null || error is not null)
        {
            return error switch
            {
                "Unauthorized" => Unauthorized(),
                "NotFound" => NotFound(),
                "UserNotFound" => NotFound("User not found."),
                "CharacterNotFound" => NotFound("Character not found."),
                "RoomAlreadyHasMember" => BadRequest("Room already has a member."),
                "UserAlreadyInRoom" => BadRequest("Player is already in a room. Please dissolve the current room first."),
                _ => BadRequest(error)
            };
        }

        return Ok(detail);
    }

    [HttpDelete("{roomId:int}")]
    public async Task<IActionResult> DeleteRoom(int roomId)
    {
        var (success, error) = await roomService.DeleteRoomAsync(roomId, GetBearerToken());

        if (!success)
        {
            if (error == "Unauthorized")
            {
                return Unauthorized();
            }

            if (error == "NotFound")
            {
                return NotFound();
            }

            if (error == "UserNotFound" || error == "CharacterNotFound")
            {
                return NotFound(error);
            }

            if (error == "NotOwner")
            {
                return StatusCode(403, "Only the room owner can dismiss the room.");
            }

            return BadRequest(error);
        }

        return NoContent();
    }

    private string? GetBearerToken()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authorization[prefix.Length..].Trim();
    }
}
