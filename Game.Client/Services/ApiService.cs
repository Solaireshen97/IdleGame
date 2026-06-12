using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Auth;

namespace Game.Client.Services;

public class ApiService(HttpClient httpClient, UserSessionService userSessionService)
{
    public async Task<List<RoomSummaryResponse>?> GetRoomsAsync()
    {
        return await httpClient.GetFromJsonAsync<List<RoomSummaryResponse>>("api/rooms");
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

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> CreateRoomAsync(string monsterType)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/rooms", requiresAuth: true);
        request.Content = JsonContent.Create(new CreateRoomRequest { MonsterType = monsterType });
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

    public async Task<RoomDetailResponse?> JoinRoomAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, $"api/rooms/{roomId}/join", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomDetailResponse>();
    }

    public async Task<bool> DeleteRoomAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Delete, $"api/rooms/{roomId}", requiresAuth: true);
        var response = await httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<BattleResult?> StartBattleAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/start", requiresAuth: true);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await userSessionService.ClearToken();
            }

            return null;
        }

        return await response.Content.ReadFromJsonAsync<BattleResult>();
    }

    public async Task<RoomDetailResponse?> ResetBattleAsync(int roomId)
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

            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomDetailResponse>();
    }

    public async Task<RoomDetailResponse?> HealAsync(int roomId)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/heal", requiresAuth: true);
        request.Content = JsonContent.Create(new BattleRequest { RoomId = roomId });
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

    public async Task<(CharacterSummaryResponse? Response, string? ErrorMessage)> CreateCharacterAsync(string name)
    {
        var request = await CreateRequestAsync(HttpMethod.Post, "api/user/characters", requiresAuth: true);
        request.Content = JsonContent.Create(new CreateCharacterRequest { Name = name });
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
