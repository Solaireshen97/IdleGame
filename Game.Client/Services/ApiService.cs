using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Shop;
using Game.Shared.Dtos.Gathering;
using Game.Shared.Dtos.Production;
using Game.Shared.Dtos.Professions;

namespace Game.Client.Services;

public partial class ApiService
{
    public async Task<ProfessionProgressResponse?> GetProfessionProgressAsync(string professionCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"api/professions/{Uri.EscapeDataString(professionCode)}", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<ProfessionProgressResponse>(response) : null;
    }

    public async Task<(ProfessionProgressResponse? Progress, string? ErrorMessage)> SpendProfessionTalentAsync(
        string professionCode, string nodeCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"api/professions/{Uri.EscapeDataString(professionCode)}/talents/{Uri.EscapeDataString(nodeCode)}", requiresAuth: true);
        return await ReadProfessionResultAsync(await SendTrackedAsync(request));
    }

    public async Task<(ProfessionProgressResponse? Progress, string? ErrorMessage)> RefundProfessionTalentAsync(
        string professionCode, string nodeCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"api/professions/{Uri.EscapeDataString(professionCode)}/talents/{Uri.EscapeDataString(nodeCode)}/refund", requiresAuth: true);
        return await ReadProfessionResultAsync(await SendTrackedAsync(request));
    }

    public async Task<(ProfessionProgressResponse? Progress, string? ErrorMessage)> ResetProfessionTalentsAsync(string professionCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"api/professions/{Uri.EscapeDataString(professionCode)}/talents/reset", requiresAuth: true);
        return await ReadProfessionResultAsync(await SendTrackedAsync(request));
    }

    private async Task<(ProfessionProgressResponse? Progress, string? ErrorMessage)> ReadProfessionResultAsync(HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            if (response.IsSuccessStatusCode)
                return (await ReadContextResponseAsync<ProfessionProgressResponse>(response), null);
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error.StartsWith("TalentRefundBlocked:", StringComparison.Ordinal) ? error["TalentRefundBlocked:".Length..] : error switch
            {
                "ProfessionLevelTooLow" => "专业等级还未达到要求。",
                "TalentPointsExhausted" => "当前没有可用的专业天赋点。",
                "TalentAtMaximum" => "这个天赋已经达到上限。",
                "TalentNotLearned" => "这个天赋还没有投入点数。",
                "TalentPrerequisiteMissing" => "请先点满要求的前置天赋等级。",
                "ProfessionTalentLocked" => "当前专业任务进行中，请结束任务后再修改该专业天赋。",
                "ConcurrencyConflict" => "专业数据刚刚变化，请刷新后重试。",
                "TalentNotFound" or "ProfessionNotFound" => "这个专业天赋已不存在，请刷新页面。",
                _ => "专业天赋操作失败，请稍后重试。"
            });
        }
    }

    public async Task<ProductionOverviewResponse?> GetProductionAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/production", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<ProductionOverviewResponse>(response) : null;
    }

    public async Task<(ProductionOverviewResponse? Response, string? ErrorMessage)> StartProductionAsync(
        int characterId, string recipeCode, int? targetCycles = null, string? requestId = null)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/production/start", requiresAuth: true);
        request.Content = JsonContent.Create(new StartProductionRequest
        {
            CharacterId = characterId, RecipeCode = recipeCode, TargetCycles = targetCycles,
            RequestId = requestId ?? Guid.NewGuid().ToString("N")
        });
        return await ReadProductionResultAsync(await SendTrackedAsync(request));
    }

    public async Task<(ProductionOverviewResponse? Response, string? ErrorMessage)> StopProductionAsync(int taskId)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"api/production/{taskId}/stop", requiresAuth: true);
        return await ReadProductionResultAsync(await SendTrackedAsync(request));
    }

    private async Task<(ProductionOverviewResponse? Response, string? ErrorMessage)> ReadProductionResultAsync(
        HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            if (response.IsSuccessStatusCode)
                return (await ReadContextResponseAsync<ProductionOverviewResponse>(response), null);
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "ActiveCharacterChanged" => "当前角色已切换，请刷新炼金页面。",
                "CharacterBusy" or "ProductionBusy" => "已有进行中的炼金任务，请先停止当前配方。",
                "RecipeLocked" => "该角色尚未完成配方的战斗解锁条件。",
                "LevelTooLow" => "角色等级不足。",
                "InvalidTargetCycles" => "制作批数须为 1 至 4320。",
                "InvalidRequestId" or "RequestIdConflict" or "RequestIdReused" => "请求信息已变化，请刷新后重试。",
                "InsufficientMaterials" => "当前角色背包中的材料不足，请先在药田种植并收获。",
                "ConcurrencyConflict" => "任务刚刚发生变化，请刷新后重试。",
                "RecipeNotFound" or "TaskNotFound" => "配方或任务不存在，请刷新页面。",
                _ => "生产操作失败，请稍后重试。"
            });
        }
    }

    public async Task<GatheringOverviewResponse?> GetGatheringAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/gathering", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<GatheringOverviewResponse>(response) : null;
    }

    public async Task<(GatheringOverviewResponse? Response, string? ErrorMessage)> StartGatheringAsync(
        int characterId, string pointCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/gathering/start", requiresAuth: true);
        request.Content = JsonContent.Create(new StartGatheringRequest { CharacterId = characterId, PointCode = pointCode });
        return await ReadGatheringResultAsync(await SendTrackedAsync(request));
    }

    public async Task<(GatheringOverviewResponse? Response, string? ErrorMessage)> StopGatheringAsync(int taskId)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"api/gathering/{taskId}/stop", requiresAuth: true);
        return await ReadGatheringResultAsync(await SendTrackedAsync(request));
    }

    private async Task<(GatheringOverviewResponse? Response, string? ErrorMessage)> ReadGatheringResultAsync(
        HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            if (response.IsSuccessStatusCode)
                return (await ReadContextResponseAsync<GatheringOverviewResponse>(response), null);
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "ActiveCharacterChanged" => "当前角色已切换，请刷新采集页面。",
                "CharacterBusy" => "这个角色已有进行中的战斗、采集或生产任务。",
                "PointLocked" => "该角色尚未完成采集点的战斗解锁条件。",
                "NoGatheringOpportunity" => "该角色没有这个采集点的剩余机会，请先完成对应精英讨伐。",
                "LevelTooLow" => "角色等级不足。",
                "GatheringLevelTooLow" => "采集专业等级不足。",
                "ConcurrencyConflict" => "任务刚刚发生变化，请刷新后重试。",
                "PointNotFound" or "TaskNotFound" => "采集点或任务不存在，请刷新页面。",
                _ => "采集操作失败，请稍后重试。"
            });
        }
    }

    public async Task<ShopResponse?> GetShopAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/shop", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<ShopResponse>(response) : null;
    }

    public async Task<(ShopResponse? Response, string? ErrorMessage)> PurchaseShopItemAsync(
        int characterId, string code, int quantity, string? requestId = null)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/shop/purchase", requiresAuth: true);
        request.Content = JsonContent.Create(new PurchaseShopItemRequest
        {
            CharacterId = characterId, Code = code, Quantity = quantity,
            RequestId = requestId ?? Guid.NewGuid().ToString("N")
        });
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        if (!response.IsSuccessStatusCode)
        {
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "InsufficientGold" => "金币不足。",
                "ActiveCharacterChanged" => "当前角色已切换，请刷新商店。",
                "InvalidQuantity" => "购买数量无效。",
                "InventoryLimitReached" => "该角色的道具数量已达到上限。",
                "ProductNotFound" => "商品已下架，请刷新商店。",
                "ProductLocked" => "当前角色尚未完成此商品的战斗解锁条件。",
                "InvalidRequestId" or "RequestIdConflict" or "RequestIdReused" => "请求信息已变化，请刷新后重试。",
                "ConcurrencyConflict" => "余额或背包刚刚发生变化，请重试。",
                _ => "购买失败，请稍后重试。"
            });
        }
        return (await ReadContextResponseAsync<ShopResponse>(response), null);
    }

    public async Task<(DungeonExchangeResultResponse? Response, string? ErrorMessage)> ExchangeDungeonRewardAsync(
        int characterId, string offerCode, string? requestId = null)
    {
        var receipt = await BeginEconomicRequestAsync($"exchange:{characterId}:{offerCode.Trim().ToLowerInvariant()}", requestId);
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/shop/exchange", requiresAuth: true);
        request.Content = JsonContent.Create(new ExchangeDungeonWeaponRequest
        {
            CharacterId = characterId, OfferCode = offerCode, RequestId = receipt.Id
        });
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        if (!response.IsSuccessStatusCode)
        {
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "InsufficientDungeonCurrency" => "副本徽记不足。",
                "ActiveCharacterChanged" => "当前角色已切换，请刷新兑换所。",
                "ExchangeOfferNotFound" => "兑换项目已下架，请刷新兑换所。",
                "ConcurrencyConflict" => "材料或背包刚刚发生变化，请重试。",
                _ => "兑换失败，请稍后重试。"
            });
        }
        var result = await ReadContextResponseAsync<DungeonExchangeResultResponse>(response);
        if (result is not null) CompleteEconomicRequest(receipt);
        return (result, null);
    }

    public async Task<string?> SetQuickSkillCastAsync(int characterId, bool isEnabled)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, $"api/user/characters/{characterId}/quick-skill-cast", requiresAuth: true, scope: ApiRequestScope.Account);
        request.Content = JsonContent.Create(new SetQuickSkillCastRequest { IsEnabled = isEnabled });
        using var response = await SendTrackedAsync(request);
        if (response.IsSuccessStatusCode) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<List<RoomSummaryResponse>?> GetRoomsAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/rooms", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<List<RoomSummaryResponse>>(response) : null;
    }

    public Task<List<RegionSummaryResponse>?> GetRegionsAsync() => GetCatalogRegionsAsync();

    public Task<List<DungeonSummaryResponse>?> GetDungeonsAsync() => GetMergedDungeonsAsync();

    public async Task<DungeonSummaryResponse?> GetDungeonAsync(int dungeonId, int depthLevel = 1)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"api/dungeons/{dungeonId}?depthLevel={depthLevel}", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<DungeonSummaryResponse>(response) : null;
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> JoinRoomAsync(int roomId, int slotIndex, int? characterId = null, Game.Shared.Dtos.Formations.LoadoutSelection? selection = null, string? requestId = null)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/operations", requiresAuth: true, scope: characterId.HasValue ? ApiRequestScope.Room : ApiRequestScope.CurrentCharacter);
        request.Content = JsonContent.Create(new SubmitRoomOperationRequest { Kind = Game.Shared.Enums.RoomOperationKind.Join, SlotIndex = slotIndex, CharacterId = characterId, LoadoutSelection = selection, RequestId = requestId });
        return await HandleRoomDetailResponseAsync(await SendTrackedAsync(request), "加入房间失败。");
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> SetRoomVisibilityAsync(int roomId, bool isPublic)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, $"api/rooms/{roomId}/visibility", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new SetRoomVisibilityRequest { IsPublic = isPublic });
        using var response = await SendTrackedAsync(request);
        if ((int)response.StatusCode >= 500) return (null, "房间服务暂时不可用，请稍后重试。");
        return await HandleRoomDetailResponseAsync(response, "更新房间开放状态失败。");
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> LeaveRoomAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/operations", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new SubmitRoomOperationRequest { Kind = Game.Shared.Enums.RoomOperationKind.Leave });
        return await HandleRoomDetailResponseAsync(await SendTrackedAsync(request), "离开房间失败。");
    }

    public async Task<(SetSlotAutoResponse? Response, string? ErrorMessage)> SetSlotAutoAsync(int roomId, int slotIndex, bool isAutoEnabled)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/slots/{slotIndex}/auto", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new SetSlotAutoRequest { SlotIndex = slotIndex, IsAutoEnabled = isAutoEnabled });
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "配置 Auto 失败。" : error);
        }

        return (await ReadContextResponseAsync<SetSlotAutoResponse>(response), null);
    }

    public async Task<RoomLoadResult> LoadRoomDetailAsync(int roomId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, $"api/rooms/{roomId}", requiresAuth: true, scope: ApiRequestScope.Room);
            using var response = await SendTrackedAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
                return new(null, RoomLoadStatus.Unauthorized);
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                return new(null, RoomLoadStatus.Unavailable);
            if (!response.IsSuccessStatusCode)
                return new(null, RoomLoadStatus.RetryableError);

            var room = await response.Content.ReadFromJsonAsync<RoomDetailResponse>(timeout.Token);
            if (!IsRequestContextCurrent(request)) return new(null, RoomLoadStatus.RetryableError);
            return room is null
                ? new(null, RoomLoadStatus.RetryableError)
                : room.RoomId != roomId
                    ? new(null, RoomLoadStatus.Unavailable)
                    : new(room, RoomLoadStatus.Success);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new(null, RoomLoadStatus.RetryableError);
        }
    }

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId) =>
        (await LoadRoomDetailAsync(roomId)).Room;

    public async Task<(BattleResult? Result, string? ErrorMessage)> SyncBattleAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/sync", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode) return (null, await response.Content.ReadAsStringAsync());
        return (await ReadContextResponseAsync<BattleResult>(response), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> CreateRoomAsync(CreateRoomRequest options, int? characterId = null, Game.Shared.Dtos.Formations.LoadoutSelection? selection = null, string? requestId = null)
    {
        if (characterId.HasValue) options.CharacterId = characterId;
        if (selection is not null) options.LoadoutSelection = selection;
        if (requestId is not null) options.RequestId = requestId;
        var request = await CreateRequestAsync(HttpMethod.Post, "api/rooms", requiresAuth: true, scope: options.CharacterId.HasValue ? ApiRequestScope.Room : ApiRequestScope.CurrentCharacter);
        request.Content = JsonContent.Create(options);
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "创建房间失败。";
            }
            return (null, TranslateFormationAdmissionError(errorMessage));
        }

        return (await ReadContextResponseAsync<RoomDetailResponse>(response), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> AssignRoomSlotAsync(int roomId, int slotIndex, int characterId, Game.Shared.Dtos.Formations.LoadoutSelection? selection = null, string? requestId = null)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/operations", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new SubmitRoomOperationRequest { Kind = Game.Shared.Enums.RoomOperationKind.Assign, SlotIndex = slotIndex, CharacterId = characterId, LoadoutSelection = selection, RequestId = requestId });
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "上阵角色失败。";
            }

            return (null, TranslateFormationAdmissionError(errorMessage));
        }

        return (await ReadContextResponseAsync<RoomDetailResponse>(response), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> RemoveRoomSlotAsync(int roomId, int slotIndex)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/operations", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new SubmitRoomOperationRequest { Kind = Game.Shared.Enums.RoomOperationKind.Remove, SlotIndex = slotIndex });
        var response = await SendTrackedAsync(request);
        return await HandleRoomDetailResponseAsync(response, "移除角色失败。");
    }

    public async Task<List<RoomOperationResponse>?> GetRoomOperationsAsync(int roomId)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"api/rooms/{roomId}/operations", requiresAuth: true, scope: ApiRequestScope.Room);
        using var response = await SendTrackedAsync(request);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<List<RoomOperationResponse>>(response) : null;
    }

    public async Task<string?> CancelRoomOperationAsync(int roomId, int operationId)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, $"api/rooms/{roomId}/operations/{operationId}", requiresAuth: true, scope: ApiRequestScope.Room);
        using var response = await SendTrackedAsync(request);
        return response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync();
    }

    public async Task<bool> DeleteRoomAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Delete, $"api/rooms/{roomId}", requiresAuth: true, scope: ApiRequestScope.Room);
        var response = await SendTrackedAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> HandleRoomDetailResponseAsync(HttpResponseMessage response, string fallbackMessage)
    {
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            var error = await response.Content.ReadAsStringAsync();
            error = error.Trim('"') switch
            {
                "CharacterAlreadyInRoom" => "当前角色已在另一个战斗中，请先离开原房间。",
                "StoryMapLocked" => "这个地点尚未开放，请先在冒险日志中完成并回报前置委托。",
                "DungeonDepthLocked" => "这个账号尚未开放房间的深层层级，请先通关前一层。",
                "InvalidDungeonDepth" => "房间的深层层级暂不可用。",
                _ => error
            };
            return (null, string.IsNullOrWhiteSpace(error) ? fallbackMessage : TranslateFormationAdmissionError(error));
        }

        return (await ReadContextResponseAsync<RoomDetailResponse>(response), null);
    }

    public async Task<(BattleResult? Result, string? ErrorMessage)> ExecuteRoundAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/round", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            if (response.StatusCode == HttpStatusCode.Conflict &&
                response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                var conflictResult = await ReadContextResponseAsync<BattleResult>(response);
                if (conflictResult is not null)
                {
                    return (conflictResult, conflictResult.RoomStatus == Game.Shared.Enums.RoomStatus.Cooldown
                        ? "回合冷却中，请等待后再试。"
                        : "本场战斗已结束，请重置战斗后再试。");
                }
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "战斗请求失败。";
            }

            return (null, TranslateFormationAdmissionError(errorMessage));
        }

        return (await ReadContextResponseAsync<BattleResult>(response), null);
    }

    public Task<(BattleResult? Result, string? ErrorMessage)> StartBattleAsync(int roomId) => ExecuteRoundAsync(roomId);

    public Task<(BattleResult? Result, string? ErrorMessage)> StartPreparationAsync(
        int roomId, int expectedRoundNumber, int expectedRunSequence) =>
        SendPreparationRequestAsync("prepare", roomId, expectedRoundNumber, expectedRunSequence);

    public Task<(BattleResult? Result, string? ErrorMessage)> CancelPreparationAsync(
        int roomId, int expectedRoundNumber, int expectedRunSequence) =>
        SendPreparationRequestAsync("cancel-prepare", roomId, expectedRoundNumber, expectedRunSequence);

    private async Task<(BattleResult? Result, string? ErrorMessage)> SendPreparationRequestAsync(
        string action, int roomId, int expectedRoundNumber, int expectedRunSequence)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"api/battle/{action}", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new BattleRequest
        {
            RoomId = roomId,
            ExpectedRoundNumber = expectedRoundNumber,
            ExpectedRunSequence = expectedRunSequence
        });
        using var response = await SendTrackedAsync(request);
        if (response.IsSuccessStatusCode)
            return (await ReadContextResponseAsync<BattleResult>(response), null);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await ClearResponseSessionAsync(response);
            return (null, "登录已失效，请重新登录。");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (null, "房间或角色已不存在，请返回大厅查看。");

        var error = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            try
            {
                using var json = JsonDocument.Parse(error);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var result = json.RootElement.Deserialize<BattleResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    return (result, "回合或战斗状态已变化，请按当前状态重试。");
                }
            }
            catch (JsonException) { }
        }
        return (null, error.Trim().Trim('"') switch
        {
            "StaleRound" or "ConcurrencyConflict" => "回合或战斗状态已变化，请按当前状态重试。",
            "PreparationCancellationDenied" => "当前角色正在自动准备，请先关闭 Auto 后再取消准备。",
            "NoOwnedAliveCharacters" => "你在房间中没有存活角色，无法操作准备。",
            "RoomClosed" => "本次任务已结束，无法继续操作准备。",
            "BattleOver" => "本场战斗已结束，无法继续操作准备。",
            "WaveTransition" => "正在切换敌人，请稍后操作准备。",
            "NotInRoom" => "你已不在这个房间中，请返回大厅查看。",
            "AlreadyPrepared" => "你的角色已准备，请查看当前状态。",
            _ => action == "cancel-prepare" ? "取消准备失败，请稍后重试。" : "准备失败，请稍后重试。"
        });
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> ResetBattleAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/reset", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "重置战斗失败。";
            }

            return (null, TranslateFormationAdmissionError(errorMessage));
        }

        return (await ReadContextResponseAsync<RoomDetailResponse>(response), null);
    }

    public async Task<(AuthResponse? Response, string? ErrorMessage)> RegisterAsync(RegisterRequest request)
    {
        var response = await httpClient.PostAsJsonAsync("api/user/register", request);
        return await HandleAuthResponseAsync(response);
    }

    public async Task<(AuthResponse? Response, string? ErrorMessage)> LoginAsync(LoginRequest request)
    {
        var response = await httpClient.PostAsJsonAsync("api/user/login", request);
        return await HandleAuthResponseAsync(response);
    }

    public Task<CurrentUserResponse?> GetCurrentUserAsync(bool forceRefresh = false) =>
        ReadCachedAsync("user", ReadCurrentUserAsync, forceRefresh);

    private async Task<CurrentUserResponse?> ReadCurrentUserAsync()
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, "api/user/me", requiresAuth: true);
            using var response = await SendTrackedAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await ClearResponseSessionAsync(response);
                }

                return null;
            }

            return await ReadContextResponseAsync<CurrentUserResponse>(response);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    public Task<CurrentCharacterResponse?> GetCurrentCharacterAsync(bool forceRefresh = false) =>
        ReadCachedAsync("character", ReadCurrentCharacterAsync, forceRefresh);

    private async Task<CurrentCharacterResponse?> ReadCurrentCharacterAsync()
    {
        var request = await CreateRequestAsync(HttpMethod.Get, "api/user/character", requiresAuth: true);
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            return null;
        }

        return await ReadContextResponseAsync<CurrentCharacterResponse>(response);
    }

    public Task<List<CharacterSummaryResponse>?> GetCurrentCharactersAsync(bool forceRefresh = false) =>
        ReadCachedAsync("characters", ReadCurrentCharactersAsync, forceRefresh);

    private async Task<List<CharacterSummaryResponse>?> ReadCurrentCharactersAsync()
    {
        var request = await CreateRequestAsync(HttpMethod.Get, "api/user/characters", requiresAuth: true);
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            return null;
        }

        return await ReadContextResponseAsync<List<CharacterSummaryResponse>>(response);
    }

    public Task<List<ProfessionResponse>?> GetProfessionsAsync() => GetCatalogProfessionsAsync();

    public Task<(CharacterCombatProfessionsResponse? Response, string? ErrorMessage)> GetCharacterProfessionsAsync(int characterId) =>
        ReadCachedResultAsync($"professions:{characterId}", () => SendCharacterProfessionRequestAsync(HttpMethod.Get, $"api/skills/professions/{characterId}"));

    public Task<(CharacterCombatProfessionsResponse? Response, string? ErrorMessage)> SwitchCharacterProfessionAsync(int characterId, string professionCode) =>
        SendCharacterProfessionRequestAsync(HttpMethod.Post, $"api/skills/professions/{characterId}/switch",
            new SwitchCombatProfessionRequest { ProfessionCode = professionCode });

    private async Task<(CharacterCombatProfessionsResponse? Response, string? ErrorMessage)> SendCharacterProfessionRequestAsync(
        HttpMethod method, string url, object? body = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "职业操作失败。" : error.Trim().Trim('"') switch
            {
                "UnsupportedSkillLoadoutVersion" => "技能配置版本暂不支持，请更新后重试。",
                _ => error.Trim().Trim('"')
            });
        }
        return await ReadCharacterResultAsync<CharacterCombatProfessionsResponse>(response, method, "professions", value => value.CharacterId);
    }

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> GetCharacterSkillsAsync(int characterId) =>
        ReadCachedResultAsync($"skills:{characterId}", () => SendSkillRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/skills"));

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> SetSkillSlotAsync(
        int characterId, int slotIndex, SetSkillSlotRequest configuration) =>
        SendSkillRequestAsync(HttpMethod.Put, $"api/user/characters/{characterId}/skills/{slotIndex}", configuration);

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> SetSkillAutoAsync(
        int characterId, int slotIndex, SetSkillAutoRequest configuration) =>
        SendSkillRequestAsync(HttpMethod.Patch, $"api/user/characters/{characterId}/skills/{slotIndex}/auto", configuration);

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> SwapSkillSlotsAsync(
        int characterId, int fromSlotIndex, int toSlotIndex) =>
        SendSkillRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/skills/swap",
             new SwapSkillSlotsRequest { FromSlotIndex = fromSlotIndex, ToSlotIndex = toSlotIndex });

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> UnlockSkillTalentAsync(
        int characterId, string nodeCode) =>
        SendSkillRequestAsync(HttpMethod.Post,
            $"api/user/characters/{characterId}/skills/talents/{Uri.EscapeDataString(nodeCode)}/unlock");

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> RefundSkillTalentAsync(
        int characterId, string nodeCode) =>
        SendSkillRequestAsync(HttpMethod.Post,
            $"api/user/characters/{characterId}/skills/talents/{Uri.EscapeDataString(nodeCode)}/refund");

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> ResetSkillTalentsAsync(int characterId) =>
        SendSkillRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/skills/talents/reset");

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> PromoteCharacterAsync(
        int characterId, string professionCode) =>
        SendSkillRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/profession/promote",
            new PromoteCharacterRequest { ProfessionCode = professionCode });

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> QueueSkillAsync(
        int roomId, int characterId, int skillSlotIndex, bool isQueued, int? targetCharacterId = null)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/skill", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new QueueSkillRequest
        {
            RoomId = roomId,
            CharacterId = characterId,
            SkillSlotIndex = skillSlotIndex,
            IsQueued = isQueued,
            TargetCharacterId = targetCharacterId
        });
        using var response = await SendTrackedAsync(request);
        return await HandleRoomDetailResponseAsync(response, "安排技能失败。");
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> QueueSoulImprintAsync(
        int roomId, int characterId, bool isQueued)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/soul-imprint", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new QueueSoulImprintRequest
        {
            RoomId = roomId, CharacterId = characterId, IsQueued = isQueued
        });
        using var response = await SendTrackedAsync(request);
        return await HandleRoomDetailResponseAsync(response, "安排魂印失败。");
    }

    private async Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> SendSkillRequestAsync(
        HttpMethod method, string url, object? configuration = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true);
        if (configuration is not null) request.Content = JsonContent.Create(configuration);
        using var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "技能操作失败。" : error);
        }
        return await ReadCharacterResultAsync<CharacterSkillsResponse>(response, method, "skills", value => value.CharacterId);
    }

    public Task<(CharacterConsumablesResponse? Response, string? ErrorMessage)> GetCharacterConsumablesAsync(int characterId, bool forceRefresh = false) =>
        ReadCachedResultAsync($"consumables:{characterId}", () => SendConsumableRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/consumables"), forceRefresh);

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> GetCharacterWeaponsAsync(int characterId, bool forceRefresh = false) =>
        ReadCachedResultAsync($"weapons:{characterId}", () => SendWeaponRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/weapons"), forceRefresh);

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> SetWeaponSlotAsync(
        int characterId, int slotIndex, int? weaponId) =>
        SendWeaponRequestAsync(HttpMethod.Put, $"api/user/characters/{characterId}/weapons/slots/{slotIndex}",
            new SetWeaponSlotRequest { WeaponId = weaponId });

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> SetWeaponLockAsync(
        int characterId, int weaponId, bool isLocked) =>
        SendWeaponRequestAsync(HttpMethod.Put, $"api/user/characters/{characterId}/weapons/{weaponId}/lock",
            new SetWeaponLockRequest { IsLocked = isLocked });

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> SellWeaponsAsync(
        int characterId, params int[] weaponIds) =>
        PreviewAndExecuteWeaponsAsync(characterId, "sell", weaponIds);

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> DismantleWeaponsAsync(
        int characterId, params int[] weaponIds) =>
        PreviewAndExecuteWeaponsAsync(characterId, "dismantle", weaponIds);

    public async Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> EnhanceWeaponSkillAsync(
        int characterId, int weaponId, int skillSlotIndex, string? requestId = null)
    {
        var receipt = await BeginEconomicRequestAsync($"enhance:{characterId}:{weaponId}:{skillSlotIndex}", requestId);
        var result = await SendWeaponRequestAsync(HttpMethod.Post,
            $"api/user/characters/{characterId}/weapons/{weaponId}/skills/{skillSlotIndex}/enhance",
            new EnhanceWeaponSkillRequest { RequestId = receipt.Id });
        if (result.Response is not null) CompleteEconomicRequest(receipt);
        return result;
    }

    public async Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> UpgradeWeaponQualityAsync(
        int characterId, int weaponId, int materialWeaponId, bool useUniversalStone = false)
    {
        var receipt = await BeginEconomicRequestAsync($"upgrade:{characterId}:{weaponId}:{materialWeaponId}:{useUniversalStone}", null);
        var result = await SendWeaponRequestAsync(HttpMethod.Post,
            $"api/user/characters/{characterId}/weapons/{weaponId}/quality/upgrade",
            new UpgradeWeaponQualityRequest { MaterialWeaponId = materialWeaponId, UseUniversalStone = useUniversalStone, RequestId = receipt.Id });
        if (result.Response is not null) CompleteEconomicRequest(receipt);
        return result;
    }

    public async Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> CraftWeaponBreakthroughStoneAsync(
        int characterId, int tier, int quantity = 1)
    {
        var receipt = await BeginEconomicRequestAsync($"craft-breakthrough:{characterId}:{tier}:{quantity}", null);
        var result = await SendWeaponRequestAsync(HttpMethod.Post,
            $"api/user/characters/{characterId}/weapons/breakthrough-stones/craft",
            new CraftWeaponBreakthroughStoneRequest { Tier = tier, Quantity = quantity, RequestId = receipt.Id });
        if (result.Response is not null) CompleteEconomicRequest(receipt);
        return result;
    }

    private async Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> SendWeaponRequestAsync(
        HttpMethod method, string url, object? configuration = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true, scope: ApiRequestScope.ExplicitCharacter);
        if (configuration is not null) request.Content = JsonContent.Create(configuration);
        using var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            var error = await response.Content.ReadAsStringAsync();
            if (error.Trim().Trim('"') == "UnknownWeaponSkill") return (null, "该武器技能定义暂不可用，无法强化。");
            if (error.Trim().Trim('"') == "InventoryCodeConflict") return (null, "突破材料代码存在冲突，原有物品已保留，暂不能合成。");
            return (null, string.IsNullOrWhiteSpace(error) ? "武器操作失败。" : error);
        }
        return await ReadCharacterResultAsync<CharacterWeaponsResponse>(response, method, "weapons", value => value.CharacterId);
    }

    public Task<(CharacterSoulImprintsResponse? Response, string? ErrorMessage)> GetCharacterSoulImprintsAsync(
        int characterId, bool forceRefresh = false) =>
        ReadCachedResultAsync($"soul-imprints:{characterId}", () => SendSoulImprintRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/soul-imprints"), forceRefresh);

    public Task<(CharacterSoulImprintsResponse? Response, string? ErrorMessage)> SetEquippedSoulImprintAsync(
        int characterId, int? soulImprintId) =>
        SendSoulImprintRequestAsync(HttpMethod.Put,
            $"api/user/characters/{characterId}/soul-imprints/equipped",
            new SetSoulImprintRequest { SoulImprintId = soulImprintId });

    public Task<(CharacterSoulImprintsResponse? Response, string? ErrorMessage)> SetSoulImprintLockAsync(
        int characterId, int soulImprintId, bool isLocked) =>
        SendSoulImprintRequestAsync(HttpMethod.Put,
            $"api/user/characters/{characterId}/soul-imprints/{soulImprintId}/lock",
            new SetSoulImprintLockRequest { IsLocked = isLocked });

    public Task<(CharacterSoulImprintsResponse? Response, string? ErrorMessage)> SetSoulImprintAutoAsync(
        int characterId, int soulImprintId, bool autoUseEnabled, string? condition = null, int? threshold = null) =>
        SendSoulImprintRequestAsync(HttpMethod.Put,
            $"api/user/characters/{characterId}/soul-imprints/{soulImprintId}/auto",
            new SetSoulImprintAutoRequest { AutoUseEnabled = autoUseEnabled,
                AutoConditionOverride = condition, AutoHpThresholdPercent = threshold });

    public Task<(CharacterSoulImprintsResponse? Response, string? ErrorMessage)> DismantleSoulImprintsAsync(
        int characterId, params int[] soulImprintIds) =>
        PreviewAndExecuteSoulImprintsAsync(characterId, soulImprintIds);

    private async Task<(CharacterSoulImprintsResponse? Response, string? ErrorMessage)> SendSoulImprintRequestAsync(
        HttpMethod method, string url, object? configuration = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true, scope: ApiRequestScope.ExplicitCharacter);
        if (configuration is not null) request.Content = JsonContent.Create(configuration);
        using var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "LoadoutLocked" => "角色已入场，请先离场或移出角色后再调整魂印。",
                var message when message.StartsWith("FormationItemReferenced:", StringComparison.Ordinal) => $"该魂印被编队“{message["FormationItemReferenced:".Length..]}”引用，请先替换或移除编队中的引用，再进行分解。",
                "SoulImprintEquipped" => "请先卸下魂印再进行分解。",
                "SoulImprintLocked" => "已锁定的魂印不能分解。",
                "SoulImprintNotOwned" => "该魂印已不在当前角色背包中。",
                "InvalidAutoCondition" => "请选择有效的自动释放条件。",
                "InvalidHpThreshold" => "生命值阈值需要设置在 1% 到 100% 之间。",
                "ConcurrencyConflict" => "魂印状态刚刚发生变化，请刷新后重试。",
                _ => "魂印操作失败，请稍后重试。"
            });
        }
        return await ReadCharacterResultAsync<CharacterSoulImprintsResponse>(response, method, "soul-imprints", value => value.CharacterId);
    }

    public Task<(CharacterConsumablesResponse? Response, string? ErrorMessage)> SetConsumableSlotAsync(
        int characterId, int slotIndex, SetConsumableSlotRequest configuration) =>
        SendConsumableRequestAsync(HttpMethod.Put, $"api/user/characters/{characterId}/consumables/{slotIndex}", configuration);

    public Task<(CharacterConsumablesResponse? Response, string? ErrorMessage)> SetConsumableAutoAsync(
        int characterId, int slotIndex, SetConsumableAutoRequest configuration) =>
        SendConsumableRequestAsync(HttpMethod.Put, $"api/user/characters/{characterId}/consumables/{slotIndex}/auto", configuration);

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> QueueConsumableAsync(
        int roomId, int characterId, int consumableSlotIndex, bool isQueued,
        int expectedRoundNumber, int expectedRunSequence)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/consumable", requiresAuth: true, scope: ApiRequestScope.Room);
        request.Content = JsonContent.Create(new QueueConsumableRequest
        {
            RoomId = roomId,
            CharacterId = characterId,
            ConsumableSlotIndex = consumableSlotIndex,
            IsQueued = isQueued,
            ExpectedRoundNumber = expectedRoundNumber,
            ExpectedRunSequence = expectedRunSequence
        });
        using var response = await SendTrackedAsync(request);
        return await HandleRoomDetailResponseAsync(response, "安排战斗道具失败。");
    }

    private async Task<(CharacterConsumablesResponse? Response, string? ErrorMessage)> SendConsumableRequestAsync(
        HttpMethod method, string url, object? configuration = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true, scope: ApiRequestScope.ExplicitCharacter);
        if (configuration is not null) request.Content = JsonContent.Create(configuration);
        using var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "补给操作失败。" : error);
        }
        return await ReadCharacterResultAsync<CharacterConsumablesResponse>(response, method, "consumables", value => value.CharacterId);
    }

    public async Task<(CharacterSummaryResponse? Response, string? ErrorMessage)> SelectCurrentCharacterAsync(int characterId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/user/character/select", requiresAuth: true, scope: ApiRequestScope.Account);
        request.Content = JsonContent.Create(new SelectCharacterRequest { CharacterId = characterId });
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (errorMessage.Trim().Trim('"') == "ConcurrencyConflict")
                errorMessage = "角色信息刚刚变化，请刷新后重试。";
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "切换当前角色失败。";
            }

            return (null, errorMessage);
        }

        return (await ReadContextResponseAsync<CharacterSummaryResponse>(response), null);
    }

    public async Task<(CharacterSummaryResponse? Response, string? ErrorMessage)> CreateCharacterAsync(string name, string professionCode)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/user/characters", requiresAuth: true, scope: ApiRequestScope.Account);
        request.Content = JsonContent.Create(new CreateCharacterRequest { Name = name, ProfessionCode = professionCode });
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "创建角色失败。";
            }

            return (null, errorMessage);
        }

        return (await ReadContextResponseAsync<CharacterSummaryResponse>(response), null);
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteCharacterAsync(int characterId)
    {
        var request = await CreateRequestAsync(HttpMethod.Delete, $"api/user/characters/{characterId}", requiresAuth: true, scope: ApiRequestScope.Account);
        var response = await SendTrackedAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearResponseSessionAsync(response);
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (errorMessage.Trim().Trim('"') == "ConcurrencyConflict")
                errorMessage = "角色信息刚刚变化，请刷新后重试。";
            if (errorMessage.Trim('"') == "CharacterBusy")
                errorMessage = "角色正在战斗，请先结束战斗。";
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "删除角色失败。";
            }

            return (false, errorMessage);
        }

        return (true, null);
    }

    public async Task<bool> LogoutAsync()
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/user/logout", requiresAuth: true, scope: ApiRequestScope.Account);
        var response = await SendTrackedAsync(request);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await ClearResponseSessionAsync(response);
        }

        return response.IsSuccessStatusCode;
    }

    private async Task<(AuthResponse? Response, string? ErrorMessage)> HandleAuthResponseAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "认证请求失败。";
            }

            return (null, errorMessage);
        }

        var authResponse = await ReadContextResponseAsync<AuthResponse>(response);
        if (authResponse is null)
        {
            return (null, "认证响应解析失败。");
        }

        await userSessionService.SetToken(authResponse.Token);
        return (authResponse, null);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string requestUri, bool requiresAuth = false,
        ApiRequestScope scope = ApiRequestScope.CurrentCharacter)
    {
        var request = new HttpRequestMessage(method, requestUri);
        if (requiresAuth)
        {
            await AttachAuthorizationHeaderAsync(request);
            request.Options.Set(SelectionRevisionKey, CharacterSelectionRevision);
            request.Options.Set(RequestScopeKey, scope);
        }

        return request;
    }

    private async Task AttachAuthorizationHeaderAsync(HttpRequestMessage request)
    {
        var snapshot = await userSessionService.GetSnapshot();
        request.Options.Set(SessionRevisionKey, snapshot.Revision);
        if (!string.IsNullOrWhiteSpace(snapshot.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.Token);
        }
    }
}
