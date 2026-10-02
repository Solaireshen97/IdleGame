using Game.Client.Services;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Game.Client.Components;

public partial class FormationEditor
{
    [Parameter, EditorRequired] public SaveFormationRequest Draft { get; set; } = default!;
    [Parameter] public string Section { get; set; } = "details";
    [Parameter] public int InitialSlot { get; set; } = 1;
    [Parameter] public string? InitialSupplyCode { get; set; }
    [Parameter] public bool IsNew { get; set; }
    [Parameter] public bool HasUnsavedChanges { get; set; }
    [Parameter] public FormationPreviewResponse? Preview { get; set; }
    [Parameter] public bool PreviewLoading { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool CanApply { get; set; }
    [Parameter] public bool IsApplied { get; set; }
    [Parameter] public bool IsDefault { get; set; }
    [Parameter] public bool CanDeploy { get; set; }
    [Parameter] public EventCallback OnApply { get; set; }
    [Parameter] public EventCallback OnToggleDefault { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public string? Notice { get; set; }
    [Parameter] public string? ManagementOperation { get; set; }
    [Parameter] public string? ChoicesError { get; set; }
    [Parameter] public string? ChoicesWarning { get; set; }
    [Parameter] public bool ChoicesLoading { get; set; }
    [Parameter] public EventCallback OnRetryChoices { get; set; }
    [Parameter] public List<CharacterCombatProfessionResponse> Professions { get; set; } = [];
    [Parameter] public List<CharacterWeaponResponse> Weapons { get; set; } = [];
    [Parameter] public List<ConsumableItemResponse> Supplies { get; set; } = [];
    [Parameter] public List<CharacterSoulImprintResponse> Souls { get; set; } = [];
    [Parameter] public List<FormationResponse> Formations { get; set; } = [];
    [Parameter] public int? EditingId { get; set; }
    [Parameter] public int PositionsPerElement { get; set; } = 6;
    [Parameter] public int CharacterId { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    [Parameter] public EventCallback OnTouched { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
    [Parameter] public EventCallback OnCopy { get; set; }
    [Parameter] public EventCallback OnDelete { get; set; }
    private int _selectedSlot;
    private bool _confirmDelete;
    private bool _focusDeleteConfirmation, _focusDeleteTrigger;
    private ElementReference _deleteTrigger, _keepFormationButton;
    private ElementReference _dialog;
    private InventoryItemPicker? _weaponPicker;
    private InventoryItemPicker? _supplyPicker;
    private bool _supplyLinkHandled;
    private string Title => Section switch { "skills" => "技能编成", "weapons" => "武器盘", "profession" => "选择职业", "supplies" => "战斗补给", "soul" => "魂印配置", _ => IsNew ? "新建编队" : "编队管理" };
    protected override void OnInitialized()
    {
        _selectedSlot = Math.Clamp(InitialSlot, 1, Section == "supplies" ? 3 : Section == "skills" ? 5 : 10);
        if (Section == "supplies" && Supplies.FirstOrDefault(item => item.Code == InitialSupplyCode) is { } supply)
            _selectedSlot = supply.Kind switch { "CombatBuff" => ConsumableRules.BuffPotionSlotIndex, "OperationPotion" => ConsumableRules.OperationPotionSlotIndex, _ => ConsumableRules.HealingPotionSlotIndex };
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Js.InvokeVoidAsync("inventoryUi.focusDialog", _dialog, true);
        }
        if (!_supplyLinkHandled && Section == "supplies" && !string.IsNullOrWhiteSpace(InitialSupplyCode) && ChoicesError is null && _supplyPicker is not null)
        {
            var supply = Supplies.FirstOrDefault(item => item.Code == InitialSupplyCode);
            var targetSlot = supply?.Kind switch { "CombatBuff" => ConsumableRules.BuffPotionSlotIndex, "OperationPotion" => ConsumableRules.OperationPotionSlotIndex, _ => ConsumableRules.HealingPotionSlotIndex };
            if (_selectedSlot != targetSlot) { _selectedSlot = targetSlot; StateHasChanged(); return; }
            _supplyLinkHandled = true;
            await _supplyPicker.ShowAsync(InitialSupplyCode);
        }
        if (_focusDeleteConfirmation)
        {
            _focusDeleteConfirmation = false;
            await _keepFormationButton.FocusAsync();
        }
        else if (_focusDeleteTrigger)
        {
            _focusDeleteTrigger = false;
            await _deleteTrigger.FocusAsync(preventScroll: true);
        }
    }
    private void ShowDeleteConfirmation() { _confirmDelete = true; _focusDeleteConfirmation = true; }
    private void KeepFormation() { _confirmDelete = false; _focusDeleteTrigger = true; }
    private Task RequestClose() => Busy ? Task.CompletedTask : OnCancel.InvokeAsync();
    private Task Touched() => OnTouched.InvokeAsync();
    private Task KeyDown(KeyboardEventArgs args) => args.Key == "Escape" ? RequestClose() : Task.CompletedTask;
    private LearnedSkillResponse? FindSkill(string? code) => Preview?.AvailableSkills.FirstOrDefault(s => s.Code == code)
        ?? Preview?.SkillLibrary.FirstOrDefault(s => s.Code == code);
    private IReadOnlyList<FormationSkillLibraryEntry> SkillLibraryEntries => Preview?.SkillLibrary is { Count: > 0 } library ? library
        : (Preview?.AvailableSkills ?? []).Select(skill => new FormationSkillLibraryEntry
        {
            Code = skill.Code, Name = skill.Name, Description = skill.Description, Level = skill.Level,
            IsShared = skill.IsShared, IsUnlocked = true, CanEquip = true,
            SourceProfessionCode = skill.SourceProfessionCode,
            SourceProfessionName = Professions.FirstOrDefault(p => p.Code == skill.SourceProfessionCode)?.Name ?? "职业",
            SourceProfessionLevel = Professions.FirstOrDefault(p => p.Code == skill.SourceProfessionCode)?.Level ?? Preview?.Level ?? 1,
            RequiredProfessionLevel = skill.IsShared ? SkillRules.SharedSkillUnlockLevel : skill.UnlockLevel,
            RequiredCurrentProfessionLevel = skill.IsShared ? SkillRules.SharedSkillEquipLevel : skill.UnlockLevel,
            AutoCondition = skill.AutoCondition, Effects = skill.Effects,
            CooldownRounds = skill.CooldownRounds, InitialCooldownRounds = skill.InitialCooldownRounds,
            LevelPreviews = [skill]
        }).ToList();
    private Task ChooseProfessionAsync(string code) { Draft.Loadout.ProfessionCode = code; return OnChanged.InvokeAsync(); }
    private Task ChooseGroup(ElementType element)
    {
        Draft.GroupElement = element;
        if (Formations.Any(f => f.Id != EditingId && f.GroupElement == element && f.Position == Draft.Position))
            Draft.Position = Enumerable.Range(1, PositionsPerElement).FirstOrDefault(p => !Formations.Any(f => f.Id != EditingId && f.GroupElement == element && f.Position == p), Draft.Position);
        return OnChanged.InvokeAsync();
    }
    private Task ChooseSkillAsync(string? code)
    {
        if (code is not null && (PreviewLoading || Preview?.AvailableSkills.All(s => s.Code != code) != false)) return Task.CompletedTask;
        if (code is not null && FindSkill(code)?.IsShared == true && Draft.Loadout.Skills.Any(s => s.SlotIndex != _selectedSlot && s.SkillCode != code && FindSkill(s.SkillCode)?.IsShared == true)) return Task.CompletedTask;
        var current = Draft.Loadout.Skills.Single(s => s.SlotIndex == _selectedSlot);
        var existing = code is null ? null : Draft.Loadout.Skills.FirstOrDefault(s => s.SkillCode == code && s.SlotIndex != _selectedSlot);
        if (existing is not null) (existing.SlotIndex, current.SlotIndex) = (current.SlotIndex, existing.SlotIndex);
        else if (current.SkillCode != code)
        {
            current.SkillCode = code; current.AutoConditionOverride = null;
            current.AutoHpThresholdPercent = SkillRules.DefaultAutoHpThresholdPercent;
            current.AutoUseEnabled = code is not null;
        }
        return OnChanged.InvokeAsync();
    }
    private Task MoveSkillAsync(int direction)
    {
        var from = Draft.Loadout.Skills.Single(s => s.SlotIndex == _selectedSlot);
        var to = Draft.Loadout.Skills.Single(s => s.SlotIndex == _selectedSlot + direction);
        (from.SlotIndex, to.SlotIndex) = (to.SlotIndex, from.SlotIndex); _selectedSlot += direction;
        return OnChanged.InvokeAsync();
    }
    private async Task SelectWeaponSlotAsync(int index)
    {
        _selectedSlot = index;
        // Render the selected slot before opening the shared inventory selector.
        await InvokeAsync(StateHasChanged);
        if (_weaponPicker is not null) await _weaponPicker.ShowAsync();
    }
    private IReadOnlyList<InventoryItemPicker.Option> WeaponOptions
    {
        get
        {
            var current = Draft.Loadout.Weapons.Single(w => w.SlotIndex == _selectedSlot);
            var options = Weapons.Select(w =>
            {
                var slot = Draft.Loadout.Weapons.FirstOrDefault(s => s.WeaponId == w.Id);
                var wouldEmptyMain = !current.WeaponId.HasValue && slot?.SlotIndex == WeaponRules.MainSlotIndex;
                var description = $"#{w.Id} · 攻击 {w.Attack} · 生命 {w.MaxHp} · Lv.{w.ItemLevel} · {WeaponRules.ElementName(w.Element)} · {w.QualityName}"
                    + (slot is null ? "" : $" · 槽位 {slot.SlotIndex}") + (wouldEmptyMain ? " · 不能使主武器空缺" : "");
                return new InventoryItemPicker.Option(w.Id.ToString(), w.Name, description, WeaponArt.ForCode(w.WeaponCode), wouldEmptyMain);
            }).ToList();
            if (Draft.Loadout.Weapons.FirstOrDefault(w => w.SlotIndex == _selectedSlot)?.WeaponId is int id && Weapons.All(w => w.Id != id))
                options.Insert(0, new(id.ToString(), "武器已失效", "请重新选择武器", Disabled: true));
            return options;
        }
    }
    private Task PickWeaponAsync(string value)
    {
        int? id = int.TryParse(value, out var parsed) ? parsed : null;
        var slot = Draft.Loadout.Weapons.Single(w => w.SlotIndex == _selectedSlot);
        if (!id.HasValue && slot.SlotIndex == WeaponRules.MainSlotIndex) return Task.CompletedTask;
        if (id.HasValue && !slot.WeaponId.HasValue && Draft.Loadout.Weapons.Any(w => w.SlotIndex == WeaponRules.MainSlotIndex && w.WeaponId == id)) return Task.CompletedTask;
        if (id.HasValue && Draft.Loadout.Weapons.FirstOrDefault(w => w.WeaponId == id && w.SlotIndex != slot.SlotIndex) is { } other) other.WeaponId = slot.WeaponId;
        slot.WeaponId = id;
        return OnChanged.InvokeAsync();
    }
    private IReadOnlyList<InventoryItemPicker.Option> SupplyOptions => Supplies
        .Where(s => s.Quantity > 0 && ConsumableRules.CanEquip(_selectedSlot, s.Kind))
        .Select(SupplyOption).ToList();
    private static InventoryItemPicker.Option SupplyOption(ConsumableItemResponse supply) => new(
        supply.Code, supply.Name,
        supply.Quantity > 0 ? $"库存 {supply.Quantity} · {supply.Description}" : "库存已耗尽 · 保留当前配置",
        ItemArt.ForCode(supply.Code));
    private Task PickSupplyAsync(string value)
    {
        if (!string.IsNullOrEmpty(value) && !Supplies.Any(s => s.Code == value && s.Quantity > 0 && ConsumableRules.CanEquip(_selectedSlot, s.Kind))) return Task.CompletedTask;
        var slot = Draft.Loadout.Consumables.Single(c => c.SlotIndex == _selectedSlot);
        if (slot.ItemCode != value) { slot.AutoConditionOverride = null; slot.AutoHpThresholdPercent = Supplies.FirstOrDefault(s => s.Code == value)?.DefaultAutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent; }
        slot.ItemCode = string.IsNullOrEmpty(value) ? null : value;
        return OnChanged.InvokeAsync();
    }
    private IReadOnlyList<InventoryItemPicker.Option> SoulOptions
    {
        get
        {
            var options = Souls.Select(s => new InventoryItemPicker.Option(s.Id.ToString(), s.Name, $"T{s.Tier} · {WeaponRules.ElementName(s.Element)} · #{s.Id}", ItemArt.ForCode(s.Code))).ToList();
            if (Draft.Loadout.SoulImprintId is int id && Souls.All(s => s.Id != id))
                options.Insert(0, new(id.ToString(), "魂印已失效", "请重新选择魂印", Disabled: true));
            return options;
        }
    }
    private Task PickSoulAsync(string value)
    {
        var id = int.TryParse(value, out var parsed) ? parsed : (int?)null;
        if (Draft.Loadout.SoulImprintId != id) { Draft.Loadout.SoulAutoConditionOverride = null; Draft.Loadout.SoulAutoHpThresholdPercent = SkillRules.DefaultAutoHpThresholdPercent; }
        Draft.Loadout.SoulImprintId = id;
        return OnChanged.InvokeAsync();
    }
}
