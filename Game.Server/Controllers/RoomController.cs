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
                "CharacterAlreadyInRoom" => BadRequest("Current character is already in another room."),
                _ => BadRequest("Failed to create room.")
            };
        }

        return Ok(roomDetail);
    }

    [HttpPost("{roomId:int}/join")]
    public async Task<IActionResult> JoinRoom(int roomId)
    {
        return StatusCode(StatusCodes.Status410Gone, "JoinDeprecated");
    }

    [HttpPost("{roomId:int}/slots")]
    public async Task<IActionResult> AssignSlot(int roomId, [FromBody] AssignRoomSlotRequest request)
    {
        var (detail, error) = await roomService.AssignSlotAsync(roomId, request, GetBearerToken());
        return detail is null ? RoomOperationError(error) : Ok(detail);
    }

    [HttpDelete("{roomId:int}/slots/{slotIndex:int}")]
    public async Task<IActionResult> RemoveSlot(int roomId, int slotIndex)
    {
        var (detail, error) = await roomService.RemoveSlotAsync(roomId, slotIndex, GetBearerToken());
        return detail is null ? RoomOperationError(error) : Ok(detail);
    }

    [HttpPost("{roomId:int}/main-control")]
    public async Task<IActionResult> SetMainControl(int roomId, [FromBody] SetMainControlRequest request)
    {
        var (detail, error) = await roomService.SetMainControlAsync(roomId, request, GetBearerToken());
        return detail is null ? RoomOperationError(error) : Ok(detail);
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

    private IActionResult RoomOperationError(string? error) => error switch
    {
        "Unauthorized" => Unauthorized(),
        "NotFound" => NotFound(),
        "UserNotFound" or "CharacterNotFound" => NotFound(error),
        "NotOwner" or "NotCharacterOwner" => StatusCode(StatusCodes.Status403Forbidden, error),
        "RoomCooldown" => Conflict(error),
        _ => BadRequest(error)
    };
}
