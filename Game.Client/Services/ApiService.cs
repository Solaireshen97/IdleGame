using System.Net.Http.Json;
using Game.Shared.Dtos;

namespace Game.Client.Services;

public class ApiService(HttpClient httpClient)
{
    public async Task<List<RoomSummaryResponse>?> GetRoomsAsync()
    {
        return await httpClient.GetFromJsonAsync<List<RoomSummaryResponse>>("api/rooms");
    }

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId)
    {
        return await httpClient.GetFromJsonAsync<RoomDetailResponse>($"api/rooms/{roomId}");
    }

    public async Task<RoomDetailResponse?> CreateRoomAsync(string monsterType)
    {
        var response = await httpClient.PostAsJsonAsync("api/rooms", new CreateRoomRequest { MonsterType = monsterType });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomDetailResponse>();
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
}

