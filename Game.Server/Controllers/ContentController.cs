using Game.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/content")]
public sealed class ContentController(ContentCatalogStore catalogs, RoomService rooms) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var document = await catalogs.GetAsync();
        Response.Headers.ETag = document.ETag;
        Response.Headers.CacheControl = "public, max-age=60, must-revalidate";
        if (Request.Headers.IfNoneMatch.SelectMany(value => (value ?? "").Split(','))
            .Any(value =>
            {
                var tag = value.Trim();
                if (tag.StartsWith("W/", StringComparison.Ordinal)) tag = tag[2..];
                return tag == "*" || tag == document.ETag;
            }))
            return StatusCode(StatusCodes.Status304NotModified);
        return Content(document.Json, "application/json");
    }

    [HttpGet("progress")]
    public async Task<IActionResult> GetProgress()
    {
        Response.Headers.CacheControl = "no-store";
        var authorization = Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        var progress = await rooms.GetDungeonProgressAsync(token);
        return progress.UserId == 0 ? Unauthorized() : Ok(progress);
    }
}
