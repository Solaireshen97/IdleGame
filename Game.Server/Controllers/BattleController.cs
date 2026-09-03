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
        return await ExecuteRoundAsync(request);
    }

    [HttpPost("round")]
    public async Task<IActionResult> Round([FromBody] BattleRequest request)
    {
        return await ExecuteRoundAsync(request);
    }

    private async Task<IActionResult> ExecuteRoundAsync(BattleRequest request)
    {
        var (result, error) = await battleService.ExecuteRoundAsync(request.RoomId, GetBearerToken());
        if (error is "RoundCooldown" or "BattleOver")
        {
            return Conflict(result);
        }

        if (result is null)
        {
            return error switch
            {
                "Unauthorized" => Unauthorized(),
                "UserNotFound" => NotFound("User not found."),
                "NotFound" => NotFound(),
                "CharacterNotFound" => NotFound("Character not found."),
                "MonsterNotFound" => NotFound("Monster not found."),
                "NotInRoom" => StatusCode(403, "NotInRoom"),
                "NotOwner" => StatusCode(403, "NotOwner"),
                "ConcurrencyConflict" => Conflict("ConcurrencyConflict"),
                _ => BadRequest(error)
            };
        }

        return Ok(result);
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] BattleRequest request)
    {
        var token = GetBearerToken();
        var (success, error) = await battleService.ResetBattleAsync(request.RoomId, token);
        if (!success)
        {
            return error switch
            {
                "Unauthorized" => Unauthorized(),
                "UserNotFound" => NotFound("User not found."),
                "NotFound" => NotFound(),
                "MonsterNotFound" => NotFound("Monster not found."),
                "NotInRoom" => StatusCode(403, "NotInRoom"),
                "NotOwner" => StatusCode(403, "NotOwner"),
                "ConcurrencyConflict" => Conflict("ConcurrencyConflict"),
                _ => BadRequest(error)
            };
        }

        var roomDetail = await roomService.GetRoomDetailAsync(request.RoomId, token);
        if (roomDetail is null)
        {
            return NotFound();
        }

        return Ok(roomDetail);
    }

    [HttpPost("heal")]
    public async Task<IActionResult> Heal([FromBody] BattleRequest request)
    {
        var token = GetBearerToken();
        var (success, error) = await battleService.HealCharacterAsync(request.RoomId, token);
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

            if (error == "UserNotFound" || error == "CharacterNotFound" || error == "MonsterNotFound")
            {
                return NotFound(error);
            }

            if (error == "NotInRoom")
            {
                return StatusCode(403, "NotInRoom");
            }

            if (error == "NotOwner")
            {
                return StatusCode(403, "NotOwner");
            }

            if (error == "ConcurrencyConflict")
            {
                return Conflict("ConcurrencyConflict");
            }

            return BadRequest(error);
        }

        var roomDetail = await roomService.GetRoomDetailAsync(request.RoomId, token);
        if (roomDetail is null)
        {
            return NotFound();
        }

        return Ok(roomDetail);
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
