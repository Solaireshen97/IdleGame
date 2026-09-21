using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;

namespace Game.Client.Services;

public class ApiService(HttpClient httpClient, UserSessionService userSessionService)
{
    public async Task<List<RoomSummaryResponse>?> GetRoomsAsync()
    {
        return await httpClient.GetFromJsonAsync<List<RoomSummaryResponse>>("api/rooms");
    }

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

    public async Task<(BattleResult? Result, string? ErrorMessage)> StartPreparationAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/prepare", requiresAuth: true);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
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
        var request = await CreateRequestAsync(HttpMethod.Get, "api/user/me", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
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

    private async Task<(CharacterWeaponsResponse? Response, string? ErrorMessage)> SendWeaponRequestAsync(
        HttpMethod method, string url, SetWeaponSlotRequest? configuration = null)
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

    public Task<(CharacterTalentsResponse? Response, string? ErrorMessage)> AllocateTalentAsync(int characterId, Game.Shared.Enums.TalentType type) =>
        SendTalentRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/talents/{type.ToString().ToLowerInvariant()}/allocate");

    public Task<(CharacterTalentsResponse? Response, string? ErrorMessage)> ResetTalentsAsync(int characterId) =>
        SendTalentRequestAsync(HttpMethod.Post, $"api/user/characters/{characterId}/talents/reset");

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
