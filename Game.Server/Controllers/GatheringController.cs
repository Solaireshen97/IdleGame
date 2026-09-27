using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/gathering")]
public sealed class GatheringController : ControllerBase
{
    [HttpGet]
    [HttpPost("start")]
    [HttpPost("{taskId:int}/stop")]
    public IActionResult Retired() => StatusCode(StatusCodes.Status410Gone, "GatheringReplacedByPlanting");
}
