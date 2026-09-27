using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/professions")]
public sealed class ProfessionsController : ControllerBase
{
    [HttpGet("{professionCode}")]
    [HttpPost("{professionCode}/talents/{nodeCode}")]
    [HttpPost("{professionCode}/talents/{nodeCode}/refund")]
    [HttpPost("{professionCode}/talents/reset")]
    public IActionResult Retired() => StatusCode(StatusCodes.Status410Gone, "ProfessionProgressionRetired");
}
