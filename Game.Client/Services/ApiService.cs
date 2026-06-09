using System.Net.Http.Json;
using Game.Shared.Dtos;

namespace Game.Client.Services;

public class ApiService(HttpClient httpClient)
{
    public async Task<RoomStateResponse?> GetRoomStateAsync(int roomId)
    {
        return await httpClient.GetFromJsonAsync<RoomStateResponse>($"api/rooms/{roomId}");
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
}
