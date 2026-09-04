using Game.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/dungeons")]
public class DungeonController(RoomService roomService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetDungeons() => Ok(await roomService.GetDungeonsAsync(GetBearerToken()));

    [HttpGet("{dungeonId:int}")]
    public async Task<IActionResult> GetDungeon(int dungeonId)
    {
        var dungeon = await roomService.GetDungeonAsync(dungeonId, GetBearerToken());
        return dungeon is null ? NotFound() : Ok(dungeon);
    }

    private string? GetBearerToken()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? authorization[prefix.Length..].Trim() : null;
    }
}