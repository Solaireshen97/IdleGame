using Game.Server.Services;
using Game.Shared.Dtos.Shop;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/shop")]
public sealed class ShopController(ShopService shopService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ShopResponse>> Get()
    {
        var (response, error) = await shopService.GetAsync(GetToken());
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("purchase")]
    public async Task<ActionResult<ShopResponse>> Purchase([FromBody] PurchaseShopItemRequest? request)
    {
        if (request is null) return BadRequest("Request body is required.");
        var (response, error) = await shopService.PurchaseAsync(GetToken(), request);
        return error is null ? Ok(response) : ToError(error);
    }

    [HttpPost("exchange")]
    public async Task<ActionResult<DungeonExchangeResultResponse>> Exchange(
        [FromBody] ExchangeDungeonWeaponRequest? request)
    {
        if (request is null) return BadRequest("Request body is required.");
        var (response, error) = await shopService.ExchangeAsync(GetToken(), request);
        return error is null ? Ok(response) : ToError(error);
    }

    private string? GetToken()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim() : null;
    }

    private ActionResult ToError(string error) => error switch
    {
        "Unauthorized" => Unauthorized(error),
        "UserNotFound" or "CharacterNotFound" or "ProductNotFound" or "ExchangeOfferNotFound" => NotFound(error),
        "ActiveCharacterChanged" or "ConcurrencyConflict" => Conflict(error),
        _ => BadRequest(error)
    };
}
