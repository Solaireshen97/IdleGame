using System.Net.Http.Json;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;

namespace Game.Client.Services;

public partial class ApiService
{
    private ContentCatalogCache? _contentCatalogs;
    private ContentCatalogCache ContentCatalogs => _contentCatalogs ??= new(httpClient);

    public async Task<List<ProfessionResponse>?> GetCatalogProfessionsAsync() =>
        (await ContentCatalogs.GetAsync())?.Professions;

    public async Task<List<RegionSummaryResponse>?> GetCatalogRegionsAsync() =>
        (await ContentCatalogs.GetAsync())?.Regions;

    public async Task<List<DungeonSummaryResponse>?> GetMergedDungeonsAsync()
    {
        await EnsureContextAsync();
        var revision = DataRevision;
        var catalogTask = ContentCatalogs.GetAsync();
        for (var attempt = 0; attempt < 2 && revision == DataRevision; attempt++)
        {
            var characterTask = GetCurrentCharacterAsync(forceRefresh: attempt > 0);
            var progressTask = ReadCachedAsync("dungeon-progress", ReadDungeonProgressAsync, force: attempt > 0);
            await Task.WhenAll(characterTask, catalogTask, progressTask);
            var character = await characterTask;
            var catalog = await catalogTask;
            var progress = await progressTask;
            if (revision != DataRevision || catalog is null || progress is null) return null;
            if (progress.CharacterId == character?.CharacterId)
            {
                var entries = progress.Dungeons.ToDictionary(entry => entry.DungeonId);
                return catalog.Dungeons.Select(dungeon => dungeon.WithProgress(entries.GetValueOrDefault(dungeon.DungeonId),
                    progress.CurrentCharacterLevel)).ToList();
            }
            InvalidateCharacter();
            _queries.Invalidate("dungeon-progress");
        }
        return null;
    }

    private async Task<DungeonProgressResponse?> ReadDungeonProgressAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/content/progress", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<DungeonProgressResponse>() : null;
    }
}
