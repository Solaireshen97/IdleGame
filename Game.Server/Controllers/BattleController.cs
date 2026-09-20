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

    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] BattleRequest request)
    {
        var (result, error) = await battleService.SyncAsync(request.RoomId, GetBearerToken());
        if (result is not null) return Ok(result);
        return error switch
        {
            "Unauthorized" => Unauthorized(),
            "NotFound" or "UserNotFound" or "MonsterNotFound" => NotFound(error),
            "NotInRoom" => StatusCode(403, "NotInRoom"),
            "ConcurrencyConflict" => Conflict("ConcurrencyConflict"),
            _ => BadRequest(error)
        };
    }

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare([FromBody] BattleRequest request)
    {
        var (result, error) = await battleService.StartPreparationAsync(request.RoomId, GetBearerToken());
        if (error is "RoundCooldown" or "BattleOver") return Conflict(result);
        if (result is not null) return Ok(result);
        return error switch
        {
            "Unauthorized" => Unauthorized(),
            "UserNotFound" => NotFound("User not found."),
            "NotFound" => NotFound(),
            "CharacterNotFound" => NotFound("Character not found."),
            "MonsterNotFound" => NotFound("Monster not found."),
            "NotInRoom" => StatusCode(403, "NotInRoom"),
            "ConcurrencyConflict" => Conflict("ConcurrencyConflict"),
            _ => BadRequest(error)
        };
    }

    [HttpPost("round")]
    public async Task<IActionResult> Round([FromBody] BattleRequest request)
    {
        return await ExecuteRoundAsync(request);
    }

    private async Task<IActionResult> ExecuteRoundAsync(BattleRequest request)
    {
        var (result, error) = await battleService.ExecuteRoundAsync(request.RoomId, GetBearerToken());
        if (error is "RoundCooldown" or "BattleOver" or "PreparationRequired")
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
                "BattleNotOver" or "RepeatBattlePending" => Conflict(error),
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

    [HttpPost("consumable")]
    public async Task<IActionResult> QueueConsumable([FromBody] QueueConsumableRequest request)
    {
        var token = GetBearerToken();
        var (success, error) = await battleService.QueueConsumableAsync(request, token);
        if (!success)
        {
            return error switch
            {
                "Unauthorized" => Unauthorized(),
                "NotFound" or "UserNotFound" or "MonsterNotFound" => NotFound(error),
                "NotInRoom" or "NotCharacterOwner" => StatusCode(403, error),
                "BattleOver" or "ConsumableCooldown" or "ConcurrencyConflict" => Conflict(error),
                _ => BadRequest(error)
            };
        }
        var detail = await roomService.GetRoomDetailAsync(request.RoomId, token);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("skill")]
    public async Task<IActionResult> QueueSkill([FromBody] QueueSkillRequest request)
    {
        var token = GetBearerToken();
        var (success, error) = await battleService.QueueSkillAsync(request, token);
        if (!success)
        {
            return error switch
            {
                "Unauthorized" => Unauthorized(),
                "NotFound" or "UserNotFound" or "MonsterNotFound" => NotFound(error),
                "NotInRoom" or "NotCharacterOwner" => StatusCode(403, error),
                "BattleOver" or "SkillCooldown" or "ConcurrencyConflict" => Conflict(error),
                _ => BadRequest(error)
            };
        }
        var detail = await roomService.GetRoomDetailAsync(request.RoomId, token);
        return detail is null ? NotFound() : Ok(detail);
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
