using System.Net;
using System.Net.Http.Json;
using Game.Shared.Dtos.Planting;
namespace Game.Client.Services;
public partial class ApiService
{
    public async Task<PlantingOverviewResponse?> GetPlantingAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/planting", requiresAuth: true);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<PlantingOverviewResponse>() : null;
    }
    public Task<(PlantingOverviewResponse? Overview, string? Error)> PlantAsync(PlantGardenRequest value) => SendGardenAsync("plant", value);
    public Task<(PlantingOverviewResponse? Overview, string? Error)> HarvestAsync(HarvestGardenRequest value) => SendGardenAsync("harvest", value);
    private async Task<(PlantingOverviewResponse? Overview, string? Error)> SendGardenAsync<T>(string action, T value)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/planting/" + action, requiresAuth: true);
        request.Content = JsonContent.Create(value);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await userSessionService.ClearToken();
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<PlantingOverviewResponse>(), null);
        if ((int)response.StatusCode >= 500) return (null, "服务器暂时未能确认操作，可再次点击相同植物安全重试。");
        var error = (await response.Content.ReadAsStringAsync()).Trim('"');
        return (null, error switch { "InvalidRequest" => "播种请求无效，请重新选择药田。", "RequestIdConflict" => "播种操作标识冲突，请重新选择药田。", "NotEnoughSeeds" => "种子不足，请先去商店购买或挑战精英。", "PlantLocked" => "请先完成对应战斗解锁。", "StalePlot" or "ConcurrencyConflict" => "药田已变化，请刷新后重试。", "PlantNotMature" => "这块药田尚未成熟。", "PlotOccupied" => "请选择空置药田。", "ActiveCharacterChanged" => "当前角色已切换，请刷新。", _ => "操作未完成，请刷新后重试。" });
    }
}


