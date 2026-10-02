using System.Text.Json;
using Game.Client.Services;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Game.Client.Pages;

public partial class Formations
{
    [Inject] private ApiService Api { get; set; } = default!;
    [Inject] private ActiveCharacterState Characters { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "formationId")] public int? RequestedFormationId { get; set; }
    [SupplyParameterFromQuery(Name = "section")] public string? RequestedSection { get; set; }
    [SupplyParameterFromQuery(Name = "return")] public string? ReturnDestination { get; set; }
    [SupplyParameterFromQuery(Name = "dungeonId")] public int? ReturnDungeonId { get; set; }
    [SupplyParameterFromQuery(Name = "depth")] public int? ReturnDepth { get; set; }
    [SupplyParameterFromQuery(Name = "itemCode")] public string? RequestedItemCode { get; set; }
    [SupplyParameterFromQuery(Name = "item")] public string? RequestedItem { get; set; }
    private string? RequestedSupplyCode => RequestedItemCode ?? RequestedItem;
    private string? ReturnToBattlePreparation => ReturnDestination == "rooms" && ReturnDungeonId is > 0
        ? $"/rooms?dungeonId={ReturnDungeonId}" + (ReturnDepth is > 0 ? $"&depth={ReturnDepth}" : string.Empty) : null;
    private string? _handledSection;
    private FormationOverviewResponse? _overview;
    private SaveFormationRequest? _editor;
    private FormationPreviewResponse? _editorPreview;
    private List<CharacterCombatProfessionResponse> _professions = [];
    private List<CharacterWeaponResponse> _weapons = [];
    private List<ConsumableItemResponse> _supplies = [];
    private List<CharacterSoulImprintResponse> _souls = [];
    private readonly Dictionary<ElementType, int> _lastSelection = [];
    private string? _message, _editorError, _editorNotice, _managementOperation, _originalEditor, _applyRequestId;
    private bool _weaponsAvailable, _suppliesAvailable, _soulsAvailable, _professionsAvailable;
    private string? ChoicesError => GetChoicesError("all");
    private string? GetChoicesError(string section)
    {
        List<string> missing = [];
        if ((section is "all" or "weapons") && !_weaponsAvailable) missing.Add("武器");
        if ((section is "all" or "supplies") && !_suppliesAvailable) missing.Add("补给");
        if ((section is "all" or "soul") && !_soulsAvailable) missing.Add("魂印");
        if ((section is "all" or "profession") && !_professionsAvailable) missing.Add("职业");
        return missing.Count == 0 ? null : $"{string.Join("、", missing)}列表暂不可用，已保存的配置与草稿均保留。";
    }
    private string _section = "details";
    private int _initialSlot = 1, _characterId, _generation, _previewGeneration;
    private int? _selectedId, _editingId, _locatedId;
    private ElementType _element;
    private bool _loading = true, _busy, _disposed, _previewLoading, _choicesLoading;
    private bool _revealSelection;
    private ElementReference _presetStrip;
    private TaskCompletionSource<bool>? _discardDecision;
    private FormationResponse? Selected => _overview?.Formations.FirstOrDefault(f => f.Id == _selectedId && f.GroupElement == _element);
    private IEnumerable<FormationResponse> GroupFormations => _overview?.Formations.Where(f => f.GroupElement == _element).OrderBy(f => f.Position) ?? Enumerable.Empty<FormationResponse>();
    private bool HasUnsavedChanges => _editor is not null && (!_editingId.HasValue || JsonSerializer.Serialize(_editor) != _originalEditor);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private int FreePosition(ElementType element) => Enumerable.Range(1, _overview?.PositionsPerElement ?? 6).FirstOrDefault(p => !_overview!.Formations.Any(f => f.GroupElement == element && f.Position == p));
    private bool IsApplied(FormationResponse f) => _overview?.AppliedFormationId == f.Id && _overview.AppliedFormationVersion == f.Version && !_overview.CurrentIsModified;
    public static string SupplySlotName(int slot) => slot == 1 ? "治疗" : slot == 2 ? "强化" : "合剂";
    private static string IssueEditor(FormationIssue issue) => issue.Section switch
    {
        "Weapons" => "weapons", "Skills" => "skills", "Consumables" => "supplies",
        "SoulImprint" => "soul", "Profession" => "profession", _ => "details"
    };
    public static string IssueLocation(FormationIssue issue) => issue.Section switch
    {
        "Weapons" => issue.SlotIndex is 1 ? "主武器" : issue.SlotIndex is int weapon ? $"武器 {weapon}" : "武器盘",
        "Skills" => issue.SlotIndex is int skill ? $"技能槽位 {skill}" : "技能",
        "Consumables" => issue.SlotIndex is int supply ? $"{SupplySlotName(supply)}补给" : "补给",
        "SoulImprint" => "魂印", "Profession" => "职业", _ => "编队"
    };
    public static string ElementSymbol(ElementType element) => element switch { ElementType.Fire => "♨", ElementType.Water => "◈", ElementType.Earth => "⬡", ElementType.Wind => "≋", ElementType.Light => "✧", _ => "☾" };
    public static string ElementStyle(ElementType element) => "--element: " + (element switch { ElementType.Fire => "#f39a77", ElementType.Water => "#78bce7", ElementType.Earth => "#d3ac77", ElementType.Wind => "#82c5a5", ElementType.Light => "#edcd83", _ => "#ba9ce1" }) + ";";

    protected override async Task OnInitializedAsync()
    {
        Characters.Changed += CharacterChanged;
        Api.InventoryChanged += InventoryChanged;
        await Characters.EnsureLoadedAsync();
        await LoadAsync();
    }
    protected override async Task OnParametersSetAsync()
    {
        if (_overview is not null && RequestedFormationId != _locatedId) LocateRequested();
        await OpenRequestedSectionAsync();
    }
    private async Task OpenRequestedSectionAsync()
    {
        var section = RequestedSection?.Trim().ToLowerInvariant() switch
        {
            "weapons" => "weapons", "skills" => "skills", "consumables" or "supplies" => "supplies",
            "soul" => "soul", "profession" => "profession", _ => null
        };
        if (section is null) { _handledSection = null; return; }
        if (_loading || _overview is null || _editor is not null) return;
        var request = $"{_characterId}/{RequestedFormationId}/{section}/{RequestedSupplyCode}";
        if (_handledSection == request) return;
        _handledSection = request;
        if (Selected is not null) OpenEditor(section);
        else await CreateNewInSectionAsync(section);
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_revealSelection && !_loading && _overview is not null)
        {
            _revealSelection = false;
            await Js.InvokeVoidAsync("formationUi.revealSelection", _presetStrip);
        }
    }
    private void LocateRequested()
    {
        _locatedId = RequestedFormationId;
        if (RequestedFormationId is int id && _overview?.Formations.FirstOrDefault(f => f.Id == id) is { } requested) SelectFormation(requested);
    }
    private void CharacterChanged() => _ = InvokeAsync(async () =>
    {
        if (!_disposed && _characterId != Characters.Current?.CharacterId) await LoadAsync();
        StateHasChanged();
    });
    private async Task LoadAsync()
    {
        var generation = ++_generation;
        var id = _characterId = Characters.Current?.CharacterId ?? 0;
        var wasEditing = HasUnsavedChanges;
        await DismissEditorAsync();
        _overview = null; _selectedId = null; _lastSelection.Clear(); _loading = true;
        _weapons = []; _supplies = []; _souls = []; _professions = [];
        _weaponsAvailable = _suppliesAvailable = _soulsAvailable = _professionsAvailable = false;
        if (id == 0) { _loading = false; return; }
        try
        {
            var overview = Api.GetFormationsAsync(id);
            var weapons = Api.GetCharacterWeaponsAsync(id);
            var supplies = Api.GetCharacterConsumablesAsync(id);
            var souls = Api.GetCharacterSoulImprintsAsync(id);
            var professions = Api.GetCharacterProfessionsAsync(id);
            await Task.WhenAll(overview, weapons, supplies, souls, professions);
            if (_disposed || generation != _generation) return;
            _overview = overview.Result.Response;
            _weapons = weapons.Result.Response?.Weapons ?? [];
            _supplies = supplies.Result.Response?.Items ?? [];
            _souls = souls.Result.Response?.SoulImprints ?? [];
            _professions = professions.Result.Response?.Professions ?? [];
            _weaponsAvailable = weapons.Result.Response is not null;
            _suppliesAvailable = supplies.Result.Response is not null;
            _soulsAvailable = souls.Result.Response is not null;
            _professionsAvailable = professions.Result.Response is not null;
            _message = overview.Result.ErrorMessage;
            if (_message is null && wasEditing) _message = "已切换角色，未保存的修改已丢弃。";
            if (_overview is not null)
            {
                var initial = _overview.Formations.FirstOrDefault(f => f.Id == RequestedFormationId)
                    ?? _overview.Formations.FirstOrDefault(f => f.Id == _overview.AppliedFormationId)
                    ?? _overview.Formations.FirstOrDefault(f => f.Id == _overview.DefaultFormationId)
                    ?? _overview.Formations.OrderBy(f => f.GroupElement).ThenBy(f => f.Position).FirstOrDefault();
                if (initial is not null) SelectFormation(initial);
                _locatedId = RequestedFormationId;
            }
        }
        catch { if (generation == _generation) _message = "读取编队失败，请重新载入。"; }
        finally { if (generation == _generation) _loading = false; }
        if (generation == _generation) await OpenRequestedSectionAsync();
    }
    private void SelectElement(ElementType element)
    {
        _element = element;
        _selectedId = GroupFormations.FirstOrDefault(f => f.Id == _lastSelection.GetValueOrDefault(element))?.Id ?? GroupFormations.FirstOrDefault()?.Id;
        _applyRequestId = null;
        _revealSelection = true;
    }
    private void SelectFormation(FormationResponse formation)
    {
        _element = formation.GroupElement; _selectedId = formation.Id; _lastSelection[_element] = formation.Id; _applyRequestId = null;
        _revealSelection = true;
    }
    private void OpenEditor(string section, int slot = 1)
    {
        if (Selected is not { } selected || _busy) return;
        _editingId = selected.Id;
        _editor = new() { Name = selected.Name, GroupElement = selected.GroupElement, Position = selected.Position, ExpectedVersion = selected.Version, Loadout = Clone(selected.Loadout) };
        Normalize(_editor.Loadout);
        _originalEditor = JsonSerializer.Serialize(_editor); _editorPreview = selected.Preview; _section = section; _initialSlot = slot; _editorError = null; _editorNotice = null;
    }
    private Task CreateNewAsync() => CreateNewInSectionAsync("details");
    private Task CreateNewInSectionAsync(string section)
    {
        if (_overview is null || _busy || FreePosition(_element) == 0) return Task.CompletedTask;
        _editingId = null; _section = section; _initialSlot = 1; _editorError = null;
        _editor = new() { Name = $"{WeaponRules.ElementName(_element)}属性编队 {FreePosition(_element)}", GroupElement = _element, Position = FreePosition(_element), Loadout = Clone(_overview.CurrentLoadout) };
        Normalize(_editor.Loadout); _originalEditor = null; _editorPreview = null;
        return RefreshEditorPreviewAsync();
    }
    private Task CopyAsync()
    {
        if (Selected is not { } selected || _busy) return Task.CompletedTask;
        var destination = Enum.GetValues<ElementType>().OrderBy(e => e == _element ? 0 : 1).FirstOrDefault(e => FreePosition(e) > 0);
        if (FreePosition(destination) == 0) { _editorError = "所有编队位置已满，请先释放一个位置。"; return Task.CompletedTask; }
        var sourceName = _editor?.Name ?? selected.Name;
        var sourceLoadout = _editor?.Loadout ?? selected.Loadout;
        const string suffix = " 副本";
        var copyName = sourceName[..Math.Min(sourceName.Length, 40 - suffix.Length)];
        if (copyName.Length > 0 && char.IsHighSurrogate(copyName[^1])) copyName = copyName[..^1];
        _editingId = null; _section = "details"; _editorError = null;
        _editor = new() { Name = copyName + suffix, GroupElement = destination, Position = FreePosition(destination), Loadout = Clone(sourceLoadout) };
        Normalize(_editor.Loadout); _originalEditor = null; _editorPreview = selected.Preview;
        return Task.CompletedTask;
    }
    private static void Normalize(CombatLoadoutDefinition loadout)
    {
        for (var i = 1; i <= WeaponRules.SlotCount; i++) if (!loadout.Weapons.Any(w => w.SlotIndex == i)) loadout.Weapons.Add(new() { SlotIndex = i });
        for (var i = 1; i <= SkillRules.SlotCount; i++) if (!loadout.Skills.Any(s => s.SlotIndex == i)) loadout.Skills.Add(new() { SlotIndex = i });
        for (var i = 1; i <= ConsumableRules.TotalSlotCount; i++) if (!loadout.Consumables.Any(s => s.SlotIndex == i)) loadout.Consumables.Add(new() { SlotIndex = i });
    }
    private async Task RefreshEditorPreviewAsync()
    {
        if (_editor is null) return;
        var editor = _editor; var generation = _generation; var previewGeneration = ++_previewGeneration;
        _previewLoading = true;
        try
        {
            var (preview, error) = await Api.PreviewFormationAsync(_characterId, Clone(editor.Loadout), editor.GroupElement);
            if (_disposed || generation != _generation || previewGeneration != _previewGeneration || !ReferenceEquals(editor, _editor)) return;
            _editorPreview = preview; _editorError = error;
        }
        catch { if (generation == _generation && ReferenceEquals(editor, _editor)) _editorError = "暂时无法更新预览，修改仍保留，请重试。"; }
        finally { if (previewGeneration == _previewGeneration) _previewLoading = false; }
    }
    private async Task SaveAsync() => await RunAsync(async () =>
    {
        if (_editor is null) return;
        var generation = _generation; var editor = _editor;
        var (saved, error) = await Api.SaveFormationAsync(_characterId, _editingId, Clone(editor));
        if (generation != _generation || !ReferenceEquals(editor, _editor)) return;
        if (saved is null) { _editorError = error ?? "保存未完成，请重试。"; return; }
        _overview!.Formations.RemoveAll(f => f.Id == saved.Id); _overview.Formations.Add(saved);
        SelectFormation(saved); await DismissEditorAsync();
        _message = $"已保存到「{saved.Name}」。";
        // Keep the confirmed response visible even if refreshing the overview fails.
        try
        {
            var refreshed = await Api.GetFormationsAsync(_characterId);
            if (generation == _generation && refreshed.Response is not null) _overview = refreshed.Response;
        }
        catch
        {
            if (generation == _generation) _message = $"已保存到「{saved.Name}」，概览暂未同步，请稍后刷新。";
        }
    });
    private async Task ApplyAsync() => await RunAsync(async () =>
    {
        if (Selected is not { } selected) return;
        var generation = _generation;
        _applyRequestId ??= Guid.NewGuid().ToString("N");
        var (overview, error) = await Api.ApplyFormationAsync(_characterId, selected.Id, new() { ExpectedVersion = selected.Version, ExpectedCharacterVersion = _overview!.CharacterVersion, RequestId = _applyRequestId });
        if (generation != _generation) return;
        if (overview is null) { ShowManagementResult(error ?? "应用失败，请重试。", true); return; }
        _overview = overview; _applyRequestId = null;
        ShowManagementResult($"「{selected.Name}」已应用到角色。");
        await Characters.RefreshAsync();
    }, "apply");
    private async Task ToggleDefaultAsync() => await RunAsync(async () =>
    {
        if (Selected is not { } selected) return;
        var generation = _generation; var clear = _overview!.DefaultFormationId == selected.Id;
        var (overview, error) = await Api.SetDefaultFormationAsync(_characterId, clear ? null : selected.Id, clear ? null : selected.Version);
        if (generation != _generation) return;
        if (overview is null) { ShowManagementResult(error ?? "默认编队设置失败，请重试。", true); return; }
        _overview = overview;
        ShowManagementResult(clear ? "已取消默认编队。" : "已设为默认编队。");
    }, "default");
    private void ShowManagementResult(string message, bool error = false)
    {
        if (_editor is null) _message = message;
        else if (error) _editorError = message;
        else _editorNotice = message;
    }
    private async Task DeleteAsync() => await RunAsync(async () =>
    {
        if (_editingId is not int id || _editor?.ExpectedVersion is not int version) return;
        var generation = _generation;
        var (overview, error) = await Api.DeleteFormationAsync(_characterId, id, version);
        if (generation != _generation) return;
        if (overview is null) { _editorError = error; return; }
        _overview = overview; await DismissEditorAsync(); SelectElement(_element); _message = "编队已删除，已入场的配置保持不变。";
    });
    private async Task RunAsync(Func<Task> action, string? managementOperation = null)
    {
        if (_busy) return;
        var generation = _generation; _busy = true; _managementOperation = managementOperation;
        _editorError = null; _editorNotice = null;
        try { await action(); }
        catch { if (generation == _generation) { if (_editor is not null) _editorError = "连接中断，修改已保留，请重试。"; else _message = "连接中断，请重试。"; } }
        finally { _busy = false; _managementOperation = null; }
    }
    private async Task CloseEditorAsync()
    {
        if (_busy) return;
        if (HasUnsavedChanges && !await ConfirmDiscardAsync()) return;
        await DismissEditorAsync();
    }
    private Task<bool> ConfirmDiscardAsync()
    {
        _discardDecision ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        StateHasChanged();
        return _discardDecision.Task;
    }
    private async Task ResolveDiscardAsync(bool discard)
    {
        var decision = _discardDecision;
        if (decision is null) return;
        _discardDecision = null;
        await Js.InvokeVoidAsync("inventoryUi.restoreFocus");
        decision.TrySetResult(discard);
    }
    private async Task DismissEditorAsync()
    {
        if (_discardDecision is not null) await ResolveDiscardAsync(false);
        if (_editor is null) return;
        _editor = null; ++_previewGeneration; _previewLoading = false; _editorError = null; _editorNotice = null;
        try { await Js.InvokeVoidAsync("inventoryUi.restoreFocus"); } catch (JSException) { }
    }
    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_busy || HasUnsavedChanges && !await ConfirmDiscardAsync()) { context.PreventNavigation(); return; }
        await DismissEditorAsync();
    }
    private void InventoryChanged(int id) { if (id == _characterId && !_disposed) _ = InvokeAsync(RefreshChoicesAsync); }
    private async Task RefreshChoicesAsync()
    {
        if (_choicesLoading || _disposed) return;
        _choicesLoading = true; var generation = _generation;
        try
        {
            var weapons = Api.GetCharacterWeaponsAsync(_characterId, forceRefresh: true);
            var supplies = Api.GetCharacterConsumablesAsync(_characterId, forceRefresh: true);
            var souls = Api.GetCharacterSoulImprintsAsync(_characterId, forceRefresh: true);
            var professions = Api.GetCharacterProfessionsAsync(_characterId);
            await Task.WhenAll(weapons, supplies, souls, professions);
            if (_disposed || generation != _generation) return;
            if (weapons.Result.Response is { } w) _weapons = w.Weapons;
            if (supplies.Result.Response is { } c) _supplies = c.Items;
            if (souls.Result.Response is { } s) _souls = s.SoulImprints;
            if (professions.Result.Response is { } p) _professions = p.Professions;
            _weaponsAvailable = weapons.Result.Response is not null;
            _suppliesAvailable = supplies.Result.Response is not null;
            _soulsAvailable = souls.Result.Response is not null;
            _professionsAvailable = professions.Result.Response is not null;
            if (_editor is not null && ChoicesError is null) await RefreshEditorPreviewAsync();
            StateHasChanged();
        }
        catch
        {
            if (generation == _generation)
            {
                _weaponsAvailable = _suppliesAvailable = _soulsAvailable = _professionsAvailable = false;
            }
        }
        finally { _choicesLoading = false; }
    }
    public void Dispose() { _disposed = true; ++_generation; _discardDecision?.TrySetResult(false); Characters.Changed -= CharacterChanged; Api.InventoryChanged -= InventoryChanged; }
}
