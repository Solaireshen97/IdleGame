using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Game.Shared.Dtos;

namespace Game.Client.Services;

public sealed class ContentCatalogCache(HttpClient client, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private byte[]? _json;
    private string? _etag;
    private DateTimeOffset _checkAt;
    private Task<byte[]?>? _pending;

    public async Task<ContentCatalogResponse?> GetAsync()
    {
        Task<byte[]?> read;
        lock (_gate)
        {
            if (_json is not null && _clock.GetUtcNow() < _checkAt)
                read = Task.FromResult<byte[]?>(_json);
            else if (_pending is { IsCompleted: false }) read = _pending;
            else read = _pending = LoadAsync(_json, _etag);
        }
        var json = await read;
        // Pages can adjust previews without mutating a cached definition or
        // another concurrent reader's copy, including all nested collections.
        return json is null ? null : JsonSerializer.Deserialize<ContentCatalogResponse>(json, JsonOptions);
    }

    private async Task<byte[]?> LoadAsync(byte[]? current, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/content");
        if (etag is not null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await client.SendAsync(request);
        byte[]? updated;
        if (response.StatusCode == HttpStatusCode.NotModified && current is not null) updated = current;
        else
        {
            if (!response.IsSuccessStatusCode) return null;
            updated = await response.Content.ReadAsByteArrayAsync();
            var payload = JsonSerializer.Deserialize<ContentCatalogResponse>(updated, JsonOptions);
            if (payload is null || payload.SchemaVersion != 1 || string.IsNullOrWhiteSpace(payload.Version))
                return null;
            etag = response.Headers.ETag?.ToString() ?? $"\"{payload.Version}\"";
        }
        lock (_gate)
        {
            _json = updated;
            _etag = etag;
            _checkAt = _clock.GetUtcNow() + TimeSpan.FromSeconds(60);
        }
        return updated;
    }
}
