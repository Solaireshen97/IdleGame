using Game.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/skills")]
public sealed class SkillController(SkillService skillService) : ControllerBase
{
    [HttpGet("professions")]
    public IActionResult GetProfessions() => Ok(skillService.GetProfessions());
}
