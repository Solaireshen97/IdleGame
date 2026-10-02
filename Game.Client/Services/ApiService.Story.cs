using System.Net;
using System.Net.Http.Json;
using Game.Shared.Dtos.Story;

namespace Game.Client.Services;

public partial class ApiService
{
    public const string StoryVersionChangedError = "剧情状态已变化，已重新读取；请再次点击继续。";
    public const string StoryRequestConflictError = "请求内容已变化，已重新读取；请再次点击发起新的操作。";
    public async Task<StoryOverviewResponse?> GetStoryAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/story", requiresAuth: true);
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        return response.IsSuccessStatusCode ? await ReadContextResponseAsync<StoryOverviewResponse>(response) : null;
    }

    public async Task<(StoryOverviewResponse? Response, string? ErrorMessage)> InitializeStoryAsync()
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/story/initialize", requiresAuth: true);
        return await ReadStoryResultAsync(await SendTrackedAsync(request));
    }

    public async Task<(StoryOverviewResponse? Response, string? ErrorMessage)> TurnInStoryAsync(string code, StoryTurnInRequest body)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"api/story/quests/{Uri.EscapeDataString(code)}/turn-in", requiresAuth: true);
        request.Content = JsonContent.Create(body);
        return await ReadStoryResultAsync(await SendTrackedAsync(request));
    }

    public async Task<(StoryOverviewResponse? Response, string? ErrorMessage)> SetStoryTutorialCharacterAsync(StoryTutorialCharacterRequest body)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, "api/story/tutorial-character", requiresAuth: true);
        request.Content = JsonContent.Create(body);
        return await ReadStoryResultAsync(await SendTrackedAsync(request));
    }

    private async Task<(StoryOverviewResponse? Response, string? ErrorMessage)> ReadStoryResultAsync(HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
            if (response.IsSuccessStatusCode)
            {
                var overview = await ReadContextResponseAsync<StoryOverviewResponse>(response);
                if (overview is not null && response.RequestMessage?.RequestUri?.OriginalString.Contains("/turn-in", StringComparison.Ordinal) == true)
                {
                    foreach (var characterId in overview.Quests.Where(q => q.Status == "TurnedIn" && q.ActorCharacterId.HasValue).Select(q => q.ActorCharacterId!.Value).Distinct())
                    {
                        InvalidateInventory(characterId);
                        InventoryChanged?.Invoke(characterId);
                    }
                    InvalidateCharacter();
                }
                return (overview, null);
            }
            var error = (await response.Content.ReadAsStringAsync()).Trim('"');
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                if (error is "ConcurrencyConflict" or "StoryVersionConflict" or "QuestVersionConflict") return (null, StoryVersionChangedError);
                if (error is "RequestIdConflict" or "RequestIdReused") return (null, StoryRequestConflictError);
            }
            return (null, error switch
            {
                "QuestNotReady" or "ObjectiveNotCompleted" or "StoryObjectiveIncomplete" => "委托目标尚未完成，请先完成行动。",
                "TutorialCharacterRequired" or "CharacterNotFound" or "StoryCharacterRequired" => "教学执行角色不可用，请选择一个自有角色继续教学。",
                "StoryQuestUnavailable" => "当前委托已变化，请刷新剧情后继续。",
                "ActiveCharacterChanged" => "角色或账号已切换，请重新载入剧情。",
                _ => "操作未完成，请刷新核对状态后重试；重试会沿用同一次请求。"
            });
        }
    }
}
