using System.Security.Cryptography;
using System.Text.Json;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;

namespace Game.Server.Services;

public sealed class ContentCatalogStore(IServiceScopeFactory scopes, SkillCatalog skills, WorldCatalog world, WeaponCatalog weapons)
{
    private readonly object _gate = new();
    private Task<ContentCatalogDocument>? _document;

    public Task<ContentCatalogDocument> GetAsync()
    {
        lock (_gate)
            return _document is null || _document.IsFaulted || _document.IsCanceled
                ? _document = BuildAsync() : _document;
    }

    private async Task<ContentCatalogDocument> BuildAsync()
    {
        using var scope = scopes.CreateScope();
        // Anonymous summaries provide the database IDs and fixed previews once;
        // the public DTO below explicitly excludes every personal field.
        var dungeons = await scope.ServiceProvider.GetRequiredService<RoomService>().GetDungeonsAsync(null);
        return CreateDocument(new ContentCatalogResponse
        {
            Professions = skills.BaseProfessions.Select(profession => new ProfessionResponse
            {
                Code = profession.Code, Name = profession.Name, Description = profession.Description
            }).OrderBy(profession => profession.Code == SkillRules.DefaultProfessionCode ? 0 : 1)
                .ThenBy(profession => profession.Code).ToList(),
            Regions = world.Regions.Select(region => new RegionSummaryResponse
            {
                Code = region.Code, Name = region.Name, Description = region.Description,
                MinimumLevel = region.MinimumLevel, MaximumLevel = region.MaximumLevel,
                FeaturedElement = region.FeaturedElement, FeaturedDungeonCode = region.FeaturedDungeonCode,
                FeaturedDungeonName = world.Dungeons.Single(dungeon => dungeon.Code == region.FeaturedDungeonCode).Name,
                FeaturedWeaponName = weapons.FindItem(region.FeaturedWeaponCode)!.Name
            }).ToList(),
            Dungeons = dungeons.Select(DungeonDefinitionResponse.FromSummary).ToList()
        });
    }

    public static ContentCatalogDocument CreateDocument(ContentCatalogResponse payload)
    {
        var definitions = JsonSerializer.SerializeToUtf8Bytes(new
        {
            payload.SchemaVersion, payload.Professions, payload.Regions, payload.Dungeons
        }, JsonOptions);
        payload.Version = Convert.ToHexString(SHA256.HashData(definitions)).ToLowerInvariant();
        return new(JsonSerializer.Serialize(payload, JsonOptions), payload.Version);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record ContentCatalogDocument(string Json, string Version)
{
    public string ETag => $"\"{Version}\"";
}
