using System.Net.Http.Json;
using Game.Shared.Dtos;

namespace Game.Client.Services;

public class ApiService(HttpClient httpClient)
{
    public async Task<List<RoomStateResponse>?> GetRoomsAsync()
    {
        return await httpClient.GetFromJsonAsync<List<RoomStateResponse>>("api/rooms");
    }

    public async Task<RoomStateResponse?> GetRoomStateAsync(int roomId)
    {
        return await httpClient.GetFromJsonAsync<RoomStateResponse>($"api/rooms/{roomId}");
    }

    public async Task<RoomStateResponse?> CreateRoomAsync()
    {
        var response = await httpClient.PostAsync("api/rooms", null);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomStateResponse>();
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

    public async Task<RoomStateResponse?> ResetBattleAsync(int roomId)
    {
        var response = await httpClient.PostAsJsonAsync("api/battle/reset", new BattleRequest { RoomId = roomId });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomStateResponse>();
    }

    public async Task<RoomStateResponse?> HealAsync(int roomId)
    {
        var response = await httpClient.PostAsJsonAsync("api/battle/heal", new BattleRequest { RoomId = roomId });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RoomStateResponse>();
    }
}
