using System.Net;
using System.Net.Http.Json;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;

namespace Game.Client.Services;

public partial class ApiService
{
    private static string? TranslateFormationAdmissionError(string? error) => error?.Trim().Trim('"') switch
    {
        "MainWeaponRequired" => "编队缺少主武器，请先配置主武器。",
        "SkillNotLearned" => "编队包含当前职业尚未解锁的技能，请重新校验。",
        "FormationVersionConflict" => "编队已有新版本，请重新选择后入场。",
        "LoadoutIntegrityMismatch" => "房间配置与入场记录不一致，请先修复配置。",
        "InvalidBattleLoadoutSnapshot" => "入场配置记录已失效，请离场后重新选择编队。",
        "LoadoutLocked" or "CharacterLoadoutLocked" or "CombatLoadoutLocked" => "角色已入场，请先离场或移出角色后再调整实际配置。",
        "FormationNotDeployable" => "编队暂不可出战，请查看编队校验问题。",
        _ => error
    };
    public Task<(FormationOverviewResponse? Response, string? ErrorMessage)> GetFormationsAsync(int characterId) =>
        FormationRequestAsync<FormationOverviewResponse>(characterId, HttpMethod.Get, "");
    public Task<(FormationResponse? Response, string? ErrorMessage)> SaveFormationAsync(int characterId, int? id, SaveFormationRequest draft) =>
        FormationRequestAsync<FormationResponse>(characterId, id.HasValue ? HttpMethod.Put : HttpMethod.Post, id?.ToString() ?? "", draft);
    public Task<(FormationResponse? Response, string? ErrorMessage)> CopyFormationAsync(int characterId, int id, CopyFormationRequest request) =>
        FormationRequestAsync<FormationResponse>(characterId, HttpMethod.Post, $"{id}/copy", request);
    public Task<(FormationOverviewResponse? Response, string? ErrorMessage)> DeleteFormationAsync(int characterId, int id, int version) =>
        FormationRequestAsync<FormationOverviewResponse>(characterId, HttpMethod.Delete, $"{id}?expectedVersion={version}");
    public Task<(FormationOverviewResponse? Response, string? ErrorMessage)> SetDefaultFormationAsync(int characterId, int? id, int? version) =>
        FormationRequestAsync<FormationOverviewResponse>(characterId, HttpMethod.Put, "default", new SetDefaultFormationRequest { FormationId = id, ExpectedVersion = version });
    public Task<(FormationPreviewResponse? Response, string? ErrorMessage)> PreviewFormationAsync(int characterId, CombatLoadoutDefinition loadout, ElementType element) =>
        FormationRequestAsync<FormationPreviewResponse>(characterId, HttpMethod.Post, $"preview?groupElement={element}", loadout);
    public Task<(FormationOverviewResponse? Response, string? ErrorMessage)> ApplyFormationAsync(int characterId, int id, ApplyFormationRequest request) =>
        FormationRequestAsync<FormationOverviewResponse>(characterId, HttpMethod.Post, $"{id}/apply", request);
    public Task<(FormationRecommendationResponse? Response, string? ErrorMessage)> RecommendFormationAsync(int characterId, string dungeonCode, int depthLevel) =>
        FormationRequestAsync<FormationRecommendationResponse>(characterId, HttpMethod.Get, $"recommend?dungeonCode={Uri.EscapeDataString(dungeonCode)}&depthLevel={depthLevel}");
    public Task<(FormationOverviewResponse? Response, string? ErrorMessage)> ClearFormationMemoryAsync(int characterId, string dungeonCode, int depthLevel) =>
        FormationRequestAsync<FormationOverviewResponse>(characterId, HttpMethod.Delete, $"memory?dungeonCode={Uri.EscapeDataString(dungeonCode)}&depthLevel={depthLevel}");

    private async Task<(T? Response, string? ErrorMessage)> FormationRequestAsync<T>(int characterId, HttpMethod method, string suffix, object? body = null) where T : class
    {
        using var request = await CreateRequestAsync(method, $"api/user/characters/{characterId}/formations" + (suffix.Length > 0 ? "/" + suffix : ""), requiresAuth: true, scope: ApiRequestScope.Room);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await SendTrackedAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await ClearResponseSessionAsync(response);
        if (response.IsSuccessStatusCode) return (await ReadContextResponseAsync<T>(response), null);
        var error = (await response.Content.ReadAsStringAsync()).Trim('"');
        return (null, error switch
        {
            "ConcurrencyConflict" or "FormationVersionConflict" => "编队或角色已有更新，请重新载入后再操作。",
            "FormationPositionOccupied" => "该位置已保存其他编队，请选择空位置。",
            "InvalidEncounter" => "副本信息已失效，请重新打开挑战详情。",
            "LoadoutLocked" or "CharacterBusy" or "CombatLoadoutLocked" => "角色正在活动中，保存草稿后可在离场或移出角色时应用。",
            _ => string.IsNullOrWhiteSpace(error) ? "编队操作失败，请稍后重试。" : error
        });
    }
}
