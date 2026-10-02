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
            "InvalidFormationName" => "编队名称需填写 1～40 个字符。",
            "InvalidFormationPosition" => "请选择有效的属性分组和保存位置。",
            "FormationNotFound" => "这套编队已不存在，请返回选择其他编队。",
            "InvalidProfession" => "请选择可用的职业。",
            "MainWeaponRequired" => "编队缺少主武器，请先配置主武器。",
            "WeaponNotOwned" => "部分武器已不在角色仓库，请重新选择武器。",
            "WeaponAlreadyEquipped" => "同一把武器不能占用多个槽位。",
            "SkillNotLearned" or "UnknownSkill" => "部分技能不适用于当前职业，请调整技能栏。",
            "SkillAlreadyEquipped" => "同一技能不能重复装配。",
            "SharedSkillLimitReached" => "一套编队最多装配一个共享技能。",
            "UnknownConsumable" => "部分补给已不可用，请重新选择。",
            "WrongConsumableSlot" => "补给类型与槽位不符，请重新选择。",
            "ConsumableAlreadyEquipped" => "同一补给不能重复装配。",
            "SoulImprintNotOwned" or "SoulImprintUnavailable" => "魂印已不可用，请重新选择魂印。",
            "InvalidHpThreshold" => "自动使用的血量条件需在 1%～100% 之间。",
            "InvalidAutoCondition" => "自动使用条件已失效，请重新设置。",
            "InvalidSlotIndex" or "InvalidLoadout" => "编队槽位配置有误，请重新打开编队检查。",
            "UnsupportedLoadoutVersion" or "UnsupportedLoadoutSnapshot" or "UnsupportedSkillLoadoutVersion" => "编队数据版本已更新，请刷新页面后重试。",
            "InvalidRequestId" or "RequestIdConflict" or "InvalidRequestReceipt" => "本次操作信息已变化，请返回编队后重试。",
            "InvalidEncounter" => "副本信息已失效，请重新打开挑战详情。",
            "LoadoutLocked" or "CharacterBusy" or "CombatLoadoutLocked" => "角色正在活动中，保存草稿后可在离场或移出角色时应用。",
            _ => response.StatusCode == HttpStatusCode.Unauthorized ? "登录已过期，请重新登录。" : "编队操作未完成，请稍后重试。"
        });
    }
}
