using System.Net.Http.Json;
using System.Text.Json;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Inventory;

namespace Game.Client.Services;

public partial class ApiService
{
    private readonly Dictionary<string, InventoryActionPreviewResponse> _pendingInventoryPreviews = [];

    public Task<(InventoryOverviewResponse? Response, string? ErrorMessage)> GetInventoryAsync(
        int characterId, InventoryQueryRequest query, bool forceRefresh = false, CancellationToken ct = default) =>
        ReadInventoryCachedAsync<InventoryOverviewResponse>(characterId, "list:" + InventoryQueryString(query),
            $"api/user/characters/{characterId}/inventory?{InventoryQueryString(query)}", forceRefresh, ct);

    public Task<(InventoryItemDetailDto? Response, string? ErrorMessage)> GetInventoryDetailAsync(
        int characterId, string assetKind, string key, bool forceRefresh = false, CancellationToken ct = default) =>
        ReadInventoryCachedAsync<InventoryItemDetailDto>(characterId,
            $"detail:{Uri.EscapeDataString(assetKind)}:{Uri.EscapeDataString(key)}",
            $"api/user/characters/{characterId}/inventory/details?assetKind={Uri.EscapeDataString(assetKind)}&key={Uri.EscapeDataString(key)}", forceRefresh, ct);

    public async Task<(InventoryActionPreviewResponse? Response, string? ErrorMessage)> PreviewInventoryActionAsync(
        int characterId, InventoryActionPreviewRequest value, CancellationToken ct = default)
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Post,
                $"api/user/characters/{characterId}/inventory/actions/preview", requiresAuth: true, scope: ApiRequestScope.ExplicitCharacter);
            request.Content = JsonContent.Create(value);
            using var response = await SendTrackedAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (null, await InventoryErrorAsync(response, ct));
            var result = await ReadContextResponseAsync<InventoryActionPreviewResponse>(response);
            return result is { } && result.CharacterId == characterId
                ? (result, null) : (null, "角色或会话已变化，请刷新后重试。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        { return (null, "预览暂时无法读取，请重试。"); }
    }

    public async Task<(bool Success, string? ErrorMessage)> ExecuteInventoryActionAsync(
        int characterId, InventoryActionPreviewResponse preview, CancellationToken ct = default)
    {
        if (preview.AssetKind == InventoryKinds.Weapon)
        {
            var result = await ExecuteInventoryBatchAsync<CharacterWeaponsResponse>(characterId, preview, ct);
            return (result.Response is not null, result.ErrorMessage);
        }
        if (preview.AssetKind == InventoryKinds.SoulImprint)
        {
            var result = await ExecuteInventoryBatchAsync<CharacterSoulImprintsResponse>(characterId, preview, ct);
            return (result.Response is not null, result.ErrorMessage);
        }
        return (false, "此类物品暂不支持该整理操作。");
    }

    private async Task<(T? Response, string? ErrorMessage)> ReadInventoryCachedAsync<T>(
        int characterId, string suffix, string url, bool force, CancellationToken ct) where T : class
    {
        await EnsureContextAsync();
        var key = $"inventory:{characterId}:{SessionRevision}:{suffix}";
        // Force refresh replaces an in-flight read as well as a completed value.
        // The displaced read cannot put its older result back into the cache.
        if (force) _queries.Invalidate(key);
        string? error = null;
        try
        {
            var value = await _queries.GetAsync<T>(key, async () =>
            {
                using var request = await CreateRequestAsync(HttpMethod.Get, url, requiresAuth: true, scope: ApiRequestScope.ExplicitCharacter);
                using var response = await SendTrackedAsync(request, ct);
                if (!response.IsSuccessStatusCode) { error = await InventoryErrorAsync(response, ct); return null; }
                var result = await ReadContextResponseAsync<T>(response);
                var owner = result switch
                {
                    InventoryOverviewResponse overview => overview.CharacterId,
                    InventoryItemDetailDto detail => detail.CharacterId,
                    _ => characterId
                };
                if (owner != characterId) { error = "返回的仓库归属已变化，请重新读取。"; return null; }
                return result;
            }, SummaryLifetime).WaitAsync(ct);
            return (value is null ? null : Clone(value), value is null ? error ?? "库存暂时无法读取，请刷新。" : null);
        }
        catch (ClientQueryCache.InvalidatedException) { return (null, "库存读取已失效，请刷新。"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        { return (null, "库存暂时无法读取，已保留上次同步数据。"); }
    }

    public void InvalidateInventory(int characterId) => _queries.InvalidatePrefix($"inventory:{characterId}:");

    private async Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> PreviewAndExecuteWeaponsAsync(
        int characterId, string action, IReadOnlyCollection<int> ids)
    {
        var (preview, error, key) = await LegacyInventoryPreviewAsync(characterId, InventoryKinds.Weapon, action, ids);
        if (preview is null) return (null, error);
        var result = await ExecuteInventoryBatchAsync<CharacterWeaponsResponse>(characterId, preview, default);
        if (result.Response is not null) _pendingInventoryPreviews.Remove(key);
        return result;
    }

    private async Task<(CharacterSoulImprintsResponse? Response, string? ErrorMessage)> PreviewAndExecuteSoulImprintsAsync(
        int characterId, IReadOnlyCollection<int> ids)
    {
        var (preview, error, key) = await LegacyInventoryPreviewAsync(characterId, InventoryKinds.SoulImprint, "dismantle", ids);
        if (preview is null) return (null, error);
        var result = await ExecuteInventoryBatchAsync<CharacterSoulImprintsResponse>(characterId, preview, default);
        if (result.Response is not null) _pendingInventoryPreviews.Remove(key);
        return result;
    }

    private async Task<(InventoryActionPreviewResponse? Preview, string? Error, string Key)> LegacyInventoryPreviewAsync(
        int characterId, string kind, string action, IReadOnlyCollection<int> ids)
    {
        await EnsureContextAsync();
        var key = $"{SessionRevision}:{characterId}:{kind}:{action}:{string.Join(',', ids.Order())}";
        if (_pendingInventoryPreviews.TryGetValue(key, out var pending)) return (pending, null, key);
        var result = await PreviewInventoryActionAsync(characterId, new() { AssetKind = kind, Action = action, InstanceIds = ids.ToList() });
        if (result.Response is not { Allowed: true } preview)
            return (null, result.ErrorMessage ?? "所选物品已受保护或发生变化，请刷新后重新选择。", key);
        _pendingInventoryPreviews[key] = Clone(preview);
        return (preview, null, key);
    }

    private async Task<(T? Response, string? ErrorMessage)> ExecuteInventoryBatchAsync<T>(
        int characterId, InventoryActionPreviewResponse preview, CancellationToken ct) where T : class
    {
        if (preview.CharacterId != characterId || !preview.Allowed ||
            preview.AssetKind == InventoryKinds.Weapon && preview.Action is not ("sell" or "dismantle") ||
            preview.AssetKind == InventoryKinds.SoulImprint && preview.Action != "dismantle" ||
            preview.AssetKind is not (InventoryKinds.Weapon or InventoryKinds.SoulImprint))
            return (null, "整理确认信息无效，请重新预览。");
        var snapshot = Clone(preview);
        var ids = snapshot.ExpectedVersions.Select(item => item.Id).ToList();
        var command = $"inventory:{characterId}:{snapshot.AssetKind}:{snapshot.Action}:{JsonSerializer.Serialize(snapshot.ExpectedVersions.OrderBy(item => item.Id))}:{snapshot.OutcomeFingerprint}";
        var receipt = await BeginEconomicRequestAsync(command, null);
        object body = snapshot.AssetKind == InventoryKinds.Weapon
            ? new WeaponBatchRequest { WeaponIds = ids, RequestId = receipt.Id, ExpectedVersions = snapshot.ExpectedVersions, OutcomeFingerprint = snapshot.OutcomeFingerprint }
            : new SoulImprintBatchRequest { SoulImprintIds = ids, RequestId = receipt.Id, ExpectedVersions = snapshot.ExpectedVersions, OutcomeFingerprint = snapshot.OutcomeFingerprint };
        try
        {
            var resource = snapshot.AssetKind == InventoryKinds.Weapon ? "weapons" : "soul-imprints";
            using var request = await CreateRequestAsync(HttpMethod.Post,
                $"api/user/characters/{characterId}/{resource}/{snapshot.Action}", requiresAuth: true, scope: ApiRequestScope.ExplicitCharacter);
            request.Content = JsonContent.Create(body, body.GetType());
            using var response = await SendTrackedAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // An explicit rejection is safe to re-preview. A lost/5xx response
                // retains both the original preview and request ID for receipt replay.
                if ((int)response.StatusCode < 500)
                {
                    CompleteEconomicRequest(receipt);
                    foreach (var key in _pendingInventoryPreviews.Where(pair =>
                        pair.Value.CharacterId == characterId && pair.Value.AssetKind == snapshot.AssetKind &&
                        pair.Value.Action == snapshot.Action && pair.Value.OutcomeFingerprint == snapshot.OutcomeFingerprint).Select(pair => pair.Key).ToArray())
                        _pendingInventoryPreviews.Remove(key);
                }
                return (null, await InventoryErrorAsync(response, ct));
            }
            var result = await ReadContextResponseAsync<T>(response);
            var owner = result switch
            {
                CharacterWeaponsResponse weapons => weapons.CharacterId,
                CharacterSoulImprintsResponse souls => souls.CharacterId,
                _ => characterId
            };
            if (owner != characterId) return (null, "返回的操作归属不匹配，请刷新核对原角色库存。");
            if (result is not null) CompleteEconomicRequest(receipt);
            return (result, result is null ? "会话已变化，请重新登录并刷新。" : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        { return (null, "服务器暂未确认结果，可重试本次确认以避免重复入账。"); }
    }

    private static string InventoryQueryString(InventoryQueryRequest value)
    {
        var parameters = new List<(string Key, string? Value)>
        {
            ("category", value.Category), ("search", value.Search), ("element", value.Element?.ToString()),
            ("tier", value.Tier?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("qualityRank", value.QualityRank?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("state", value.State), ("sort", value.Sort),
            ("page", value.Page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("pageSize", value.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        return string.Join('&', parameters.Where(pair => pair.Value is not null)
            .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));
    }

    private static async Task<string> InventoryErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode >= 500) return "服务器暂未确认结果，请稍后重试本次操作。";
        var error = (await response.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
        return error switch
        {
            "InvalidInventoryQuery" or "InvalidRequest" or "InvalidInventoryAction" or "InvalidInventorySelection" or "InvalidWeaponSelection" or "InvalidSoulImprintSelection" => "查询或选择信息无效，请重新选择。",
            "NotOwner" => "只能查看和管理自己的角色仓库。",
            "CharacterNotFound" => "角色已不存在，请重新选择。",
            "LoadoutLocked" => "角色已入场，请先离场或移出角色。",
            "InventoryVersionConflict" or "InventoryOutcomeChanged" or "OutcomeChanged" or "ConcurrencyConflict" or "InventoryPreviewChanged" => "物品或收益已变化，请刷新后重新预览。",
            "InventoryItemNotFound" => "物品已不在该角色仓库中，请刷新后重新选择。",
            "WeaponNotOwned" or "SoulImprintNotOwned" => "所选物品已不在该角色仓库中，请刷新后重新选择。",
            "InventoryFull" => "金币或材料数量已达到存储上限，本次整理未执行。",
            "InventoryCodeConflict" => "物品代码存在冲突，本次操作未执行，原有物品已保留。",
            "WeaponCannotBeDismantled" => "所选武器没有可返还碎片，暂不能分解。",
            "InventoryPreviewRequired" => "请先预览并确认本次整理收益。",
            "InvalidRequestId" or "RequestIdReused" => "操作编号或确认信息已变化，请重新预览。",
            "UnknownSoulImprint" => "该魂印定义暂不可用，已保留物品，暂不能分解。",
            "UnknownWeaponSkill" => "该武器技能定义暂不可用，无法强化。",
            "WeaponEquipped" or "SoulImprintEquipped" => "请先卸下所选物品，再进行整理。",
            "WeaponLocked" or "SoulImprintLocked" => "所选物品已锁定，请先解锁。",
            var message when message.StartsWith("FormationItemReferenced:", StringComparison.Ordinal) => $"物品被编队“{message["FormationItemReferenced:".Length..]}”引用，请先移除或替换引用。",
            "SessionChanged" => "登录会话已变化，请重新登录。",
            _ => string.IsNullOrWhiteSpace(error) ? "操作暂未完成，请刷新后重试。" : error
        };
    }

    private async Task NotifyInventoryMutationAsync(HttpRequestMessage request, HttpResponseMessage response)
    {
        var path = request.RequestUri is { IsAbsoluteUri: true } uri ? uri.AbsolutePath.TrimStart('/') : request.RequestUri?.OriginalString ?? "";
        int? characterId = null;
        var parts = path.Split('/');
        if (parts.Length >= 4 && parts[0] == "api" && parts[1] == "user" && parts[2] == "characters" &&
            int.TryParse(parts[3].Split('?')[0], out var id)) characterId = id;
        else if (path.StartsWith("api/shop/", StringComparison.Ordinal) || path.StartsWith("api/planting/", StringComparison.Ordinal) ||
            path.StartsWith("api/production/", StringComparison.Ordinal) || path.StartsWith("api/gathering/", StringComparison.Ordinal))
        {
            if (request.Content is not null) characterId = JsonCharacterId(await request.Content.ReadAsStringAsync());
            characterId ??= JsonCharacterId(await response.Content.ReadAsStringAsync());
        }
        if (characterId is > 0)
        {
            InvalidateInventory(characterId.Value);
            InventoryChanged?.Invoke(characterId.Value);
        }
    }

    private static int? JsonCharacterId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("characterId", StringComparison.OrdinalIgnoreCase) && property.Value.TryGetInt32(out var id)) return id;
                if (property.Name.Equals("shop", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                    return JsonCharacterId(property.Value.GetRawText());
            }
        }
        catch (JsonException) { }
        return null;
    }
}
