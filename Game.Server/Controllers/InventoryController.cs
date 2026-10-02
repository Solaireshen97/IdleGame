using Game.Server.Services;
using Game.Shared.Dtos.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/user/characters/{characterId:int}/inventory")]
public sealed class InventoryController(InventoryQuery inventory) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<InventoryOverviewResponse>> Get(int characterId, [FromQuery] InventoryQueryRequest query, CancellationToken ct)
    {
        var (response, error) = await inventory.GetAsync(Token(), characterId, query, ct);
        return error is null ? Ok(response) : Error(error);
    }

    [HttpGet("details")]
    public async Task<ActionResult<InventoryItemDetailDto>> Detail(int characterId, string assetKind, string key, CancellationToken ct)
    {
        var (response, error) = await inventory.DetailAsync(Token(), characterId, assetKind, key, ct);
        return error is null ? Ok(response) : Error(error);
    }

    [HttpPost("actions/preview")]
    public async Task<ActionResult<InventoryActionPreviewResponse>> Preview(int characterId, InventoryActionPreviewRequest request, CancellationToken ct)
    {
        var (response, error) = await inventory.PreviewAsync(Token(), characterId, request, ct);
        return error is null ? Ok(response) : Error(error);
    }

    private string? Token()
    {
        var value = Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }

    private ActionResult Error(string error) => error switch
    {
        "Unauthorized" => Unauthorized(error), "NotOwner" => StatusCode(403, error),
        "CharacterNotFound" or "InventoryItemNotFound" or "WeaponNotOwned" or "SoulImprintNotOwned" => NotFound(error),
        "ConcurrencyConflict" => Conflict(error), _ => BadRequest(error)
    };
}
