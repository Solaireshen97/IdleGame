using Game.Server.Services;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/user/characters/{characterId:int}/formations")]
public sealed class FormationController(FormationService formations) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int characterId) => Result(await formations.GetAsync(Token(), characterId));
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int characterId, int id) => Result(await formations.GetOneAsync(Token(), characterId, id));
    [HttpPost]
    public async Task<IActionResult> Create(int characterId, CreateFormationRequest request) => Result(await formations.CreateAsync(Token(), characterId, request));
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Save(int characterId, int id, SaveFormationRequest request) => Result(await formations.SaveAsync(Token(), characterId, id, request));
    [HttpPost("{id:int}/copy")]
    public async Task<IActionResult> Copy(int characterId, int id, CopyFormationRequest request) => Result(await formations.CopyAsync(Token(), characterId, id, request));
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int characterId, int id, [FromQuery] int expectedVersion) => Result(await formations.DeleteAsync(Token(), characterId, id, expectedVersion));
    [HttpPut("default")]
    public async Task<IActionResult> SetDefault(int characterId, SetDefaultFormationRequest request) => Result(await formations.SetDefaultAsync(Token(), characterId, request));
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(int characterId, CombatLoadoutDefinition request, [FromQuery] ElementType? groupElement) => Result(await formations.PreviewAsync(Token(), characterId, request, groupElement));
    [HttpPost("{id:int}/apply")]
    public async Task<IActionResult> Apply(int characterId, int id, ApplyFormationRequest request) => Result(await formations.ApplyAsync(Token(), characterId, id, request));
    [HttpGet("recommend")]
    public async Task<IActionResult> Recommend(int characterId, [FromQuery] string dungeonCode, [FromQuery] int depthLevel = 1) => Result(await formations.RecommendAsync(Token(), characterId, dungeonCode, depthLevel));
    [HttpDelete("memory")]
    public async Task<IActionResult> ClearMemory(int characterId, [FromQuery] string dungeonCode, [FromQuery] int depthLevel = 1) => Result(await formations.ClearMemoryAsync(Token(), characterId, dungeonCode, depthLevel));
    private string? Token()
    {
        var value = Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }
    private IActionResult Result<T>((T? Response, string? Error) result) where T : class => result.Error switch
    {
        null => Ok(result.Response), "Unauthorized" => Unauthorized(result.Error),
        "NotOwner" => StatusCode(403, result.Error), "CharacterNotFound" or "FormationNotFound" => NotFound(result.Error),
        "ConcurrencyConflict" or "CharacterBusy" or "LoadoutLocked" or "RequestIdConflict" => Conflict(result.Error),
        _ => BadRequest(result.Error)
    };
}
