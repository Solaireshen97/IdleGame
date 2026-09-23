using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Shop;
using Game.Shared.Dtos.Gathering;
using Game.Shared.Dtos.Production;
using Game.Shared.Dtos.Professions;

namespace Game.Client.Services;

public class ApiService(HttpClient httpClient, UserSessionService userSessionService)
{
    public async Task<ProfessionProgressResponse?> GetProfessionProgressAsync(string professionCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"api/professions/{Uri.EscapeDataString(professionCode)}", requiresAuth: true);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProfessionProgressResponse>() : null;
    }

    public async Task<(ProfessionProgressResponse? Progress, string? ErrorMessage)> SpendProfessionTalentAsync(
        string professionCode, string nodeCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"api/professions/{Uri.EscapeDataString(professionCode)}/talents/{Uri.EscapeDataString(nodeCode)}", requiresAuth: true);
        return await ReadProfessionResultAsync(await httpClient.SendAsync(request));
    }

    public async Task<(ProfessionProgressResponse? Progress, string? ErrorMessage)> ResetProfessionTalentsAsync(string professionCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"api/professions/{Uri.EscapeDataString(professionCode)}/talents/reset", requiresAuth: true);
        return await ReadProfessionResultAsync(await httpClient.SendAsync(request));
    }

    private async Task<(ProfessionProgressResponse? Progress, string? ErrorMessage)> ReadProfessionResultAsync(HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
            if (response.IsSuccessStatusCode)
                return (await response.Content.ReadFromJsonAsync<ProfessionProgressResponse>(), null);
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "ProfessionLevelTooLow" => "专业等级还未达到要求。",
                "TalentPointsExhausted" => "当前没有可用的专业天赋点。",
                "TalentAtMaximum" => "这个天赋已经达到上限。",
                "TalentPrerequisiteMissing" => "请先点满要求的前置天赋等级。",
                "ProfessionTalentLocked" => "当前专业任务进行中，请结束任务后再修改该专业天赋。",
                "ConcurrencyConflict" => "专业数据刚刚变化，请刷新后重试。",
                "TalentNotFound" or "ProfessionNotFound" => "这个专业天赋已不存在，请刷新页面。",
                _ => "加点失败，请稍后重试。"
            });
        }
    }

    public async Task<ProductionOverviewResponse?> GetProductionAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/production", requiresAuth: true);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProductionOverviewResponse>() : null;
    }

    public async Task<(ProductionOverviewResponse? Response, string? ErrorMessage)> StartProductionAsync(
        int characterId, string recipeCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/production/start", requiresAuth: true);
        request.Content = JsonContent.Create(new StartProductionRequest { CharacterId = characterId, RecipeCode = recipeCode });
        return await ReadProductionResultAsync(await httpClient.SendAsync(request));
    }

    public async Task<(ProductionOverviewResponse? Response, string? ErrorMessage)> StopProductionAsync(int taskId)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"api/production/{taskId}/stop", requiresAuth: true);
        return await ReadProductionResultAsync(await httpClient.SendAsync(request));
    }

    private async Task<(ProductionOverviewResponse? Response, string? ErrorMessage)> ReadProductionResultAsync(
        HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
            if (response.IsSuccessStatusCode)
                return (await response.Content.ReadFromJsonAsync<ProductionOverviewResponse>(), null);
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "ActiveCharacterChanged" => "当前角色已切换，请刷新炼金页面。",
                "CharacterBusy" => "这个角色已有进行中的战斗、采集或生产任务。",
                "RecipeLocked" => "该角色尚未完成配方的战斗解锁条件。",
                "LevelTooLow" => "角色等级不足。",
                "AlchemyLevelTooLow" => "炼金专业等级不足。",
                "InsufficientMaterials" => "当前角色背包中的材料不足，请先用该角色采集。",
                "ConcurrencyConflict" => "任务刚刚发生变化，请刷新后重试。",
                "RecipeNotFound" or "TaskNotFound" => "配方或任务不存在，请刷新页面。",
                _ => "生产操作失败，请稍后重试。"
            });
        }
    }

    public async Task<GatheringOverviewResponse?> GetGatheringAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/gathering", requiresAuth: true);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<GatheringOverviewResponse>() : null;
    }

    public async Task<(GatheringOverviewResponse? Response, string? ErrorMessage)> StartGatheringAsync(
        int characterId, string pointCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/gathering/start", requiresAuth: true);
        request.Content = JsonContent.Create(new StartGatheringRequest { CharacterId = characterId, PointCode = pointCode });
        return await ReadGatheringResultAsync(await httpClient.SendAsync(request));
    }

    public async Task<(GatheringOverviewResponse? Response, string? ErrorMessage)> StopGatheringAsync(int taskId)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"api/gathering/{taskId}/stop", requiresAuth: true);
        return await ReadGatheringResultAsync(await httpClient.SendAsync(request));
    }

    private async Task<(GatheringOverviewResponse? Response, string? ErrorMessage)> ReadGatheringResultAsync(
        HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
            if (response.IsSuccessStatusCode)
                return (await response.Content.ReadFromJsonAsync<GatheringOverviewResponse>(), null);
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
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ShopResponse>() : null;
    }

    public async Task<(ShopResponse? Response, string? ErrorMessage)> PurchaseShopItemAsync(
        int characterId, string code, int quantity)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/shop/purchase", requiresAuth: true);
        request.Content = JsonContent.Create(new PurchaseShopItemRequest
        {
            CharacterId = characterId, Code = code, Quantity = quantity
        });
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
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
                "ConcurrencyConflict" => "余额或背包刚刚发生变化，请重试。",
                _ => "购买失败，请稍后重试。"
            });
        }
        return (await response.Content.ReadFromJsonAsync<ShopResponse>(), null);
    }

    public async Task<(ShopResponse? Response, string? ErrorMessage)> PurchaseCharacterSlotAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/shop/character-slot", requiresAuth: true);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        if (!response.IsSuccessStatusCode)
        {
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            return (null, error switch
            {
                "InsufficientGold" => "金币不足。",
                "MaximumCharacterSlotsReached" => "角色栏位已达到上限。",
                "ConcurrencyConflict" => "金币或角色栏位刚刚发生变化，请重试。",
                _ => "角色栏位解锁失败，请稍后重试。"
            });
        }

        return (await response.Content.ReadFromJsonAsync<ShopResponse>(), null);
    }

    public async Task<(DungeonExchangeResultResponse? Response, string? ErrorMessage)> ExchangeDungeonWeaponAsync(
        int characterId, string offerCode)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/shop/exchange", requiresAuth: true);
        request.Content = JsonContent.Create(new ExchangeDungeonWeaponRequest
        {
            CharacterId = characterId, OfferCode = offerCode
        });
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
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
        return (await response.Content.ReadFromJsonAsync<DungeonExchangeResultResponse>(), null);
    }

    public async Task<List<RoomSummaryResponse>?> GetRoomsAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/rooms", requiresAuth: true);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<RoomSummaryResponse>>() : null;
    }

    public Task<List<RegionSummaryResponse>?> GetRegionsAsync() =>
        httpClient.GetFromJsonAsync<List<RegionSummaryResponse>>("api/regions");

    public async Task<List<DungeonSummaryResponse>?> GetDungeonsAsync()
    {
        var request = await CreateRequestAsync(HttpMethod.Get, "api/dungeons", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<DungeonSummaryResponse>>() : null;
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> JoinRoomAsync(int roomId, int slotIndex)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/join", requiresAuth: true);
        request.Content = JsonContent.Create(new JoinRoomRequest { SlotIndex = slotIndex });
        return await HandleRoomDetailResponseAsync(await httpClient.SendAsync(request), "加入房间失败。");
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> LeaveRoomAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Delete, $"api/rooms/{roomId}/leave", requiresAuth: true);
        return await HandleRoomDetailResponseAsync(await httpClient.SendAsync(request), "离开房间失败。");
    }

    public async Task<(SetSlotAutoResponse? Response, string? ErrorMessage)> SetSlotAutoAsync(int roomId, int slotIndex, bool isAutoEnabled)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/slots/{slotIndex}/auto", requiresAuth: true);
        request.Content = JsonContent.Create(new SetSlotAutoRequest { SlotIndex = slotIndex, IsAutoEnabled = isAutoEnabled });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "配置 Auto 失败。" : error);
        }

        return (await response.Content.ReadFromJsonAsync<SetSlotAutoResponse>(), null);
    }

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Get, $"api/rooms/{roomId}", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomDetailResponse>();
    }

    public async Task<(BattleResult? Result, string? ErrorMessage)> SyncBattleAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/sync", requiresAuth: true);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return (null, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BattleResult>(), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> CreateRoomAsync(int dungeonId, bool isRepeatBattle, bool isPreparationTimeoutEnabled)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/rooms", requiresAuth: true);
        request.Content = JsonContent.Create(new CreateRoomRequest { DungeonId = dungeonId, IsRepeatBattle = isRepeatBattle, IsPreparationTimeoutEnabled = isPreparationTimeoutEnabled });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "创建房间失败。";
            }
            return (null, errorMessage);
        }

        return (await response.Content.ReadFromJsonAsync<RoomDetailResponse>(), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> AssignRoomSlotAsync(int roomId, int slotIndex, int characterId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/slots", requiresAuth: true);
        request.Content = JsonContent.Create(new AssignRoomSlotRequest { SlotIndex = slotIndex, CharacterId = characterId });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "上阵角色失败。";
            }

            return (null, errorMessage);
        }

        return (await response.Content.ReadFromJsonAsync<RoomDetailResponse>(), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> RemoveRoomSlotAsync(int roomId, int slotIndex)
    {
        var request = await CreateRequestAsync(HttpMethod.Delete, $"api/rooms/{roomId}/slots/{slotIndex}", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        return await HandleRoomDetailResponseAsync(response, "移除角色失败。");
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> SetMainControlAsync(int roomId, int characterId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/main-control", requiresAuth: true);
        request.Content = JsonContent.Create(new SetMainControlRequest { CharacterId = characterId });
        var response = await httpClient.SendAsync(request);
        return await HandleRoomDetailResponseAsync(response, "切换主控失败。");
    }

    public async Task<bool> DeleteRoomAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Delete, $"api/rooms/{roomId}", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> HandleRoomDetailResponseAsync(HttpResponseMessage response, string fallbackMessage)
    {
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            var error = await response.Content.ReadAsStringAsync();
            if (error.Trim('"') == "CharacterAlreadyInRoom")
                error = "当前角色正在战斗、采集或生产，请先结束原任务。";
            return (null, string.IsNullOrWhiteSpace(error) ? fallbackMessage : error);
        }

        return (await response.Content.ReadFromJsonAsync<RoomDetailResponse>(), null);
    }

    public async Task<(BattleResult? Result, string? ErrorMessage)> ExecuteRoundAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/round", requiresAuth: true);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            if (response.StatusCode == HttpStatusCode.Conflict &&
                response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                var conflictResult = await response.Content.ReadFromJsonAsync<BattleResult>();
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

            return (null, errorMessage);
        }

        return (await response.Content.ReadFromJsonAsync<BattleResult>(), null);
    }

    public Task<(BattleResult? Result, string? ErrorMessage)> StartBattleAsync(int roomId) => ExecuteRoundAsync(roomId);

    public async Task<(BattleResult? Result, string? ErrorMessage)> StartPreparationAsync(int roomId, int expectedRoundNumber)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/prepare", requiresAuth: true);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId, ExpectedRoundNumber = expectedRoundNumber });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "开始回合失败。" : error);
        }

        return (await response.Content.ReadFromJsonAsync<BattleResult>(), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> ResetBattleAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/reset", requiresAuth: true);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "重置战斗失败。";
            }

            return (null, errorMessage);
        }

        return (await response.Content.ReadFromJsonAsync<RoomDetailResponse>(), null);
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

    public async Task<CurrentUserResponse?> GetCurrentUserAsync()
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, "api/user/me", requiresAuth: true);
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await userSessionService.ClearToken();
                }

                return null;
            }

            return await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
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

    public async Task<CurrentCharacterResponse?> GetCurrentCharacterAsync()
    {
        var request = await CreateRequestAsync(HttpMethod.Get, "api/user/character", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            return null;
        }

        return await response.Content.ReadFromJsonAsync<CurrentCharacterResponse>();
    }

    public async Task<List<CharacterSummaryResponse>?> GetCurrentCharactersAsync()
    {
        var request = await CreateRequestAsync(HttpMethod.Get, "api/user/characters", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            return null;
        }

        return await response.Content.ReadFromJsonAsync<List<CharacterSummaryResponse>>();
    }

    public Task<(CharacterTalentsResponse? Response, string? ErrorMessage)> GetCharacterTalentsAsync(int characterId) =>
        SendTalentRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/talents");

    public async Task<List<ProfessionResponse>?> GetProfessionsAsync() =>
        await httpClient.GetFromJsonAsync<List<ProfessionResponse>>("api/skills/professions");

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> GetCharacterSkillsAsync(int characterId) =>
        SendSkillRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/skills");

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

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> ResetSkillTalentsAsync(int characterId) =>
        SendSkillRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/skills/talents/reset");

    public Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> PromoteCharacterAsync(
        int characterId, string professionCode) =>
        SendSkillRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/profession/promote",
            new PromoteCharacterRequest { ProfessionCode = professionCode });

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> QueueSkillAsync(
        int roomId, int characterId, int skillSlotIndex, bool isQueued)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/skill", requiresAuth: true);
        request.Content = JsonContent.Create(new QueueSkillRequest
        {
            RoomId = roomId,
            CharacterId = characterId,
            SkillSlotIndex = skillSlotIndex,
            IsQueued = isQueued
        });
        using var response = await httpClient.SendAsync(request);
        return await HandleRoomDetailResponseAsync(response, "安排技能失败。");
    }

    private async Task<(CharacterSkillsResponse? Response, string? ErrorMessage)> SendSkillRequestAsync(
        HttpMethod method, string url, object? configuration = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true);
        if (configuration is not null) request.Content = JsonContent.Create(configuration);
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "技能操作失败。" : error);
        }
        return (await response.Content.ReadFromJsonAsync<CharacterSkillsResponse>(), null);
    }

    public Task<(CharacterConsumablesResponse? Response, string? ErrorMessage)> GetCharacterConsumablesAsync(int characterId) =>
        SendConsumableRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/consumables");

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> GetCharacterWeaponsAsync(int characterId) =>
        SendWeaponRequestAsync(HttpMethod.Get, $"api/user/characters/{characterId}/weapons");

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
        SendWeaponRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/weapons/sell",
            new WeaponBatchRequest { WeaponIds = weaponIds.ToList() });

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> DismantleWeaponsAsync(
        int characterId, params int[] weaponIds) =>
        SendWeaponRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/weapons/dismantle",
            new WeaponBatchRequest { WeaponIds = weaponIds.ToList() });

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> EnhanceWeaponSkillAsync(
        int characterId, int weaponId, int skillSlotIndex) =>
        SendWeaponRequestAsync(HttpMethod.Post,
            $"api/user/characters/{characterId}/weapons/{weaponId}/skills/{skillSlotIndex}/enhance");

    public Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> UpgradeWeaponQualityAsync(
        int characterId, int weaponId, int materialWeaponId) =>
        SendWeaponRequestAsync(HttpMethod.Post,
            $"api/user/characters/{characterId}/weapons/{weaponId}/quality/upgrade",
            new UpgradeWeaponQualityRequest { MaterialWeaponId = materialWeaponId });

    private async Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> SendWeaponRequestAsync(
        HttpMethod method, string url, object? configuration = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true);
        if (configuration is not null) request.Content = JsonContent.Create(configuration);
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "武器操作失败。" : error);
        }
        return (await response.Content.ReadFromJsonAsync<CharacterWeaponsResponse>(), null);
    }

    public Task<(CharacterConsumablesResponse? Response, string? ErrorMessage)> SetConsumableSlotAsync(
        int characterId, int slotIndex, SetConsumableSlotRequest configuration) =>
        SendConsumableRequestAsync(HttpMethod.Put, $"api/user/characters/{characterId}/consumables/{slotIndex}", configuration);

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> QueueConsumableAsync(
        int roomId, int characterId, int? consumableSlotIndex)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/consumable", requiresAuth: true);
        request.Content = JsonContent.Create(new QueueConsumableRequest
        {
            RoomId = roomId,
            CharacterId = characterId,
            ConsumableSlotIndex = consumableSlotIndex
        });
        using var response = await httpClient.SendAsync(request);
        return await HandleRoomDetailResponseAsync(response, "安排战斗道具失败。");
    }

    private async Task<(CharacterConsumablesResponse? Response, string? ErrorMessage)> SendConsumableRequestAsync(
        HttpMethod method, string url, SetConsumableSlotRequest? configuration = null)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true);
        if (configuration is not null) request.Content = JsonContent.Create(configuration);
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "补给操作失败。" : error);
        }
        return (await response.Content.ReadFromJsonAsync<CharacterConsumablesResponse>(), null);
    }

    private async Task<(CharacterTalentsResponse? Response, string? ErrorMessage)> SendTalentRequestAsync(HttpMethod method, string url)
    {
        using var request = await CreateRequestAsync(method, url, requiresAuth: true);
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                await userSessionService.ClearToken();
            var error = await response.Content.ReadAsStringAsync();
            return (null, string.IsNullOrWhiteSpace(error) ? "天赋操作失败。" : error);
        }

        return (await response.Content.ReadFromJsonAsync<CharacterTalentsResponse>(), null);
    }

    public async Task<(CharacterSummaryResponse? Response, string? ErrorMessage)> SelectCurrentCharacterAsync(int characterId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/user/character/select", requiresAuth: true);
        request.Content = JsonContent.Create(new SelectCharacterRequest { CharacterId = characterId });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "切换当前角色失败。";
            }

            return (null, errorMessage);
        }

        return (await response.Content.ReadFromJsonAsync<CharacterSummaryResponse>(), null);
    }

    public async Task<(CharacterSummaryResponse? Response, string? ErrorMessage)> CreateCharacterAsync(string name, string professionCode)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/user/characters", requiresAuth: true);
        request.Content = JsonContent.Create(new CreateCharacterRequest { Name = name, ProfessionCode = professionCode });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = "创建角色失败。";
            }

            return (null, errorMessage);
        }

        return (await response.Content.ReadFromJsonAsync<CharacterSummaryResponse>(), null);
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteCharacterAsync(int characterId)
    {
        var request = await CreateRequestAsync(HttpMethod.Delete, $"api/user/characters/{characterId}", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            var errorMessage = await response.Content.ReadAsStringAsync();
            if (errorMessage.Trim('"') == "CharacterBusy")
                errorMessage = "角色正在执行战斗、采集或生产任务，请先结束任务。";
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
        var request = await CreateRequestAsync(HttpMethod.Post, "api/user/logout", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await userSessionService.ClearToken();
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

        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();
        if (authResponse is null)
        {
            return (null, "认证响应解析失败。");
        }

        await userSessionService.SetToken(authResponse.Token);
        return (authResponse, null);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string requestUri, bool requiresAuth = false)
    {
        var request = new HttpRequestMessage(method, requestUri);
        if (requiresAuth)
        {
            await AttachAuthorizationHeaderAsync(request);
        }

        return request;
    }

    private async Task AttachAuthorizationHeaderAsync(HttpRequestMessage request)
    {
        var token = await userSessionService.GetToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
