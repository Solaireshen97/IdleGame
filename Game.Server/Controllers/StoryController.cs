using Game.Server.Services;
using Game.Shared.Dtos.Story;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/story")]
public sealed class StoryController(StoryService story) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Result(await story.GetAsync(Token()));
    [HttpPost("initialize")]
    public async Task<IActionResult> Initialize() => Result(await story.InitializeAsync(Token()));
    [HttpGet("map")]
    public async Task<IActionResult> Map()
    {
        var result = await story.GetAsync(Token());
        return result.Error is null ? Ok(result.Response!.MapNodes) : Result(result);
    }
    [HttpPost("quests/{code}/turn-in")]
    public async Task<IActionResult> TurnIn(string code, StoryTurnInRequest request) => Result(await story.TurnInAsync(Token(), code, request));
    [HttpPut("tutorial-character")]
    public async Task<IActionResult> ChangeCharacter(StoryTutorialCharacterRequest request) => Result(await story.ChangeCharacterAsync(Token(), request));
    private string? Token()
    {
        var value = Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }
    private IActionResult Result((StoryOverviewResponse? Response, string? Error) result) => result.Error switch
    {
        null => Ok(result.Response), "Unauthorized" => Unauthorized(result.Error),
        "NotOwner" => StatusCode(403, result.Error),
        "ConcurrencyConflict" or "RequestIdConflict" => Conflict(result.Error),
        _ => BadRequest(result.Error)
    };
}
