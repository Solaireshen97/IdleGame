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
        return await httpClient.GetFromJsonAsync<RoomDetailResponse>($"api/rooms/{roomId}");
    }

    public async Task<(RoomDetailResponse? Detail, string? ErrorMessage)> CreateRoomAsync(string monsterType)
    {
        var response = await httpClient.PostAsJsonAsync("api/rooms", new CreateRoomRequest { MonsterType = monsterType });
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
        var response = await httpClient.PostAsync($"api/rooms/{roomId}/join", null);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomDetailResponse>();
    }

    public async Task<bool> DeleteRoomAsync(int roomId)
    {
        var response = await httpClient.DeleteAsync($"api/rooms/{roomId}");
        return response.IsSuccessStatusCode;
    }

    public async Task<BattleResult?> StartBattleAsync(int roomId)
    {
        var response = await httpClient.PostAsJsonAsync("api/battle/start", new BattleRequest { RoomId = roomId });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<BattleResult>();
    }

    public async Task<RoomDetailResponse?> ResetBattleAsync(int roomId)
    {
        var response = await httpClient.PostAsJsonAsync("api/battle/reset", new BattleRequest { RoomId = roomId });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomDetailResponse>();
    }

    public async Task<RoomDetailResponse?> HealAsync(int roomId)
    {
        var response = await httpClient.PostAsJsonAsync("api/battle/heal", new BattleRequest { RoomId = roomId });
        if (!response.IsSuccessStatusCode)
        {
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
