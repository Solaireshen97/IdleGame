using Game.Server.Services;
using Game.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/regions")]
public sealed class RegionController(WorldCatalog world, WeaponCatalog weapons) : ControllerBase
{
    [HttpGet]
    public ActionResult<List<RegionSummaryResponse>> Get() => Ok(world.Regions.Select(region => new RegionSummaryResponse
    {
        Code = region.Code, Name = region.Name, Description = region.Description,
        MinimumLevel = region.MinimumLevel, MaximumLevel = region.MaximumLevel,
        FeaturedElement = region.FeaturedElement, FeaturedDungeonCode = region.FeaturedDungeonCode,
        FeaturedDungeonName = world.Dungeons.Single(dungeon => dungeon.Code == region.FeaturedDungeonCode).Name,
        FeaturedWeaponName = weapons.FindItem(region.FeaturedWeaponCode)!.Name
    }).ToList());
}
