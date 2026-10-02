using System.Text.Json;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Resolves choices without modifying tracked actors. The caller owns application transactions.</summary>
public sealed class CombatLoadoutService(GameDbContext db, SkillCatalog skills, WeaponCatalog weapons,
    ConsumableCatalog consumables, SoulImprintCatalog souls, SkillInformationService? information = null)
{
    private readonly SkillInformationService _information = information ?? new();

    public async Task<CombatLoadoutDefinition> CaptureAsync(int characterId)
    {
        var character = await db.Characters.FindAsync(characterId) ?? throw new InvalidOperationException("CharacterNotFound");
        return (await CaptureManyAsync([character]))[characterId];
    }

    public async Task<Dictionary<int, CombatLoadoutDefinition>> CaptureManyAsync(IReadOnlyList<Character> characters)
    {
        var ids = characters.Select(c => c.Id).ToArray();
        if (ids.Length == 0) return [];
        var weaponSlots = (await db.CharacterWeapons.AsNoTracking().Where(x => ids.Contains(x.CharacterId) && x.EquippedSlotIndex != null).ToListAsync()).ToLookup(x => x.CharacterId);
        var skillSlots = (await db.CharacterSkillSlots.AsNoTracking().Where(x => ids.Contains(x.CharacterId)).ToListAsync()).ToLookup(x => x.CharacterId);
        var potionSlots = (await db.CharacterConsumableSlots.AsNoTracking().Where(x => ids.Contains(x.CharacterId)).ToListAsync()).ToLookup(x => x.CharacterId);
        var soulsByCharacter = await db.CharacterSoulImprints.AsNoTracking().Where(x => ids.Contains(x.CharacterId) && x.EquippedSlotIndex == SoulImprintRules.SlotIndex).ToDictionaryAsync(x => x.CharacterId);
        return characters.ToDictionary(character => character.Id, character => new CombatLoadoutDefinition
        {
            ProfessionCode = character.ProfessionCode,
            Weapons = Enumerable.Range(1, WeaponRules.SlotCount).Select(i => new FormationWeaponChoice
                { SlotIndex = i, WeaponId = weaponSlots[character.Id].FirstOrDefault(s => s.EquippedSlotIndex == i)?.Id }).ToList(),
            Skills = Enumerable.Range(1, SkillRules.SlotCount).Select(i =>
            {
                var s = skillSlots[character.Id].FirstOrDefault(s => s.SlotIndex == i);
                return new FormationSkillChoice { SlotIndex = i, SkillCode = s?.SkillCode, AutoUseEnabled = s?.AutoUseEnabled ?? false,
                    AutoConditionOverride = s?.AutoConditionOverride, AutoHpThresholdPercent = s?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent };
            }).ToList(),
            Consumables = Enumerable.Range(1, ConsumableRules.TotalSlotCount).Select(i =>
            {
                var s = potionSlots[character.Id].FirstOrDefault(s => s.SlotIndex == i);
                return new FormationConsumableChoice { SlotIndex = i, ItemCode = s?.ItemCode, AutoUseEnabled = s?.AutoUseEnabled ?? false,
                    AutoConditionOverride = s?.AutoConditionOverride,
                    AutoHpThresholdPercent = s?.AutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent };
            }).ToList(),
            SoulImprintId = soulsByCharacter.GetValueOrDefault(character.Id)?.Id,
            SoulAutoUseEnabled = soulsByCharacter.GetValueOrDefault(character.Id)?.AutoUseEnabled ?? false,
            SoulAutoConditionOverride = soulsByCharacter.GetValueOrDefault(character.Id)?.AutoConditionOverride,
            SoulAutoHpThresholdPercent = soulsByCharacter.GetValueOrDefault(character.Id)?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent
        });
    }

    public static CombatLoadoutDefinition FromFormation(CharacterBattleFormation value) => new()
    {
        ProfessionCode = value.ProfessionCode, SoulImprintId = value.SoulImprintId, SoulAutoUseEnabled = value.SoulAutoUseEnabled,
        SoulAutoConditionOverride = value.SoulAutoConditionOverride, SoulAutoHpThresholdPercent = value.SoulAutoHpThresholdPercent,
        Weapons = value.Weapons.Select(x => new FormationWeaponChoice { SlotIndex = x.SlotIndex, WeaponId = x.WeaponId }).ToList(),
        Skills = value.Skills.Select(x => new FormationSkillChoice { SlotIndex = x.SlotIndex, SkillCode = x.SkillCode,
            AutoUseEnabled = x.AutoUseEnabled, AutoConditionOverride = x.AutoConditionOverride, AutoHpThresholdPercent = x.AutoHpThresholdPercent }).ToList(),
        Consumables = value.Consumables.Select(x => new FormationConsumableChoice { SlotIndex = x.SlotIndex, ItemCode = x.ItemCode,
            AutoUseEnabled = x.AutoUseEnabled, AutoConditionOverride = x.AutoConditionOverride, AutoHpThresholdPercent = x.AutoHpThresholdPercent }).ToList()
    };

    private sealed record PreviewData(Dictionary<string, int> Levels, List<CharacterWeapon> Weapons,
        Dictionary<string, int> Stacks, Dictionary<int, CharacterSoulImprint> Souls);

    private async Task<PreviewData> ReadPreviewDataAsync(int characterId) => new(
        await new CombatProfessionProgressStore(db).ReadLevelsAsync(characterId),
        await db.CharacterWeapons.AsNoTracking().Include(w => w.Skills).Where(w => w.CharacterId == characterId).ToListAsync(),
        await db.CharacterItemStacks.AsNoTracking().Where(x => x.CharacterId == characterId).ToDictionaryAsync(x => x.ItemCode, x => x.Quantity),
        await db.CharacterSoulImprints.AsNoTracking().Where(s => s.CharacterId == characterId).ToDictionaryAsync(s => s.Id));

    public async Task<FormationPreviewResponse> PreviewAsync(Character character, CombatLoadoutDefinition value, ElementType? group = null) =>
        Preview(character, value, group, await ReadPreviewDataAsync(character.Id));

    public async Task<List<FormationPreviewResponse>> PreviewManyAsync(Character character,
        IReadOnlyList<(CombatLoadoutDefinition Definition, ElementType? Group)> definitions)
    {
        var data = await ReadPreviewDataAsync(character.Id);
        return definitions.Select(item => Preview(character, item.Definition, item.Group, data)).ToList();
    }

    private List<FormationSkillLibraryEntry> BuildSkillLibrary(Character actor, IReadOnlyDictionary<string, int> levels)
    {
        var entries = new List<FormationSkillLibraryEntry>();
        foreach (var source in skills.BaseProfessions)
        {
            var native = string.Equals(source.Code, actor.ProfessionCode, StringComparison.OrdinalIgnoreCase);
            var definitions = native
                ? skills.SkillsAtLevel(source.Code, int.MaxValue)
                : source.SharedSkillCode is { } sharedCode ? skills.SkillLevelPreviews(sharedCode, shared: true) : [];
            foreach (var definition in definitions)
            {
                var previews = skills.SkillLevelPreviews(definition.Code, shared: !native);
                var current = native ? previews[SkillCatalog.RankFor(definition, actor.Level) - 1] : previews[0];
                var entry = DescribeLibrarySkill<FormationSkillLibraryEntry>(current);
                entry.SourceProfessionName = source.Name;
                entry.SourceProfessionLevel = native ? actor.Level : levels.GetValueOrDefault(source.Code, 1);
                entry.RequiredProfessionLevel = native ? definition.UnlockLevel : SkillRules.SharedSkillUnlockLevel;
                entry.RequiredCurrentProfessionLevel = native ? definition.UnlockLevel : SkillRules.SharedSkillEquipLevel;
                entry.IsUnlocked = entry.SourceProfessionLevel >= entry.RequiredProfessionLevel;
                entry.CanEquip = skills.Resolve(actor, definition.Code, levels) is not null;
                entry.LevelPreviews = previews.Select(DescribeLibrarySkill<LearnedSkillResponse>).ToList();
                entries.Add(entry);
            }
        }
        return entries;
    }

    private T DescribeLibrarySkill<T>(CharacterSkillDefinition definition) where T : LearnedSkillResponse, new() => new()
    {
        Code = definition.Code, Name = definition.Name, Description = _information.Description(definition),
        EffectType = SkillCatalog.PrimaryEffectType(definition), Power = SkillCatalog.PrimaryPower(definition),
        Level = definition.Level, IsShared = definition.IsShared, SourceProfessionCode = definition.ProfessionCode,
        AutoCondition = SkillCatalog.AutoConditionFor(definition), UnlockLevel = definition.UnlockLevel,
        Level2UnlockLevel = definition.Level2UnlockLevel, Level3UnlockLevel = definition.Level3UnlockLevel,
        CooldownRounds = definition.CooldownRounds, InitialCooldownRounds = definition.InitialCooldownRounds,
        Effects = _information.Effects(definition)
    };

    private FormationPreviewResponse Preview(Character character, CombatLoadoutDefinition value, ElementType? group, PreviewData data)
    {
        var result = new FormationPreviewResponse();
        void Issue(string code, string section, string message, int? slot = null, bool warning = false) =>
            result.Issues.Add(new() { Code = code, Section = section, SlotIndex = slot, Message = message, Severity = warning ? "Warning" : "Error" });
        if (value is null || value.SchemaVersion != 1 || value.Weapons is null || value.Skills is null || value.Consumables is null)
        {
            Issue("UnsupportedLoadoutSnapshot", "Formation", "编队数据版本不受支持。");
            return result;
        }
        bool SlotsValid(IEnumerable<int> indexes, int count, string section)
        {
            var list = indexes.ToList();
            if (list.Count <= count && list.All(i => i >= 1 && i <= count) && list.Distinct().Count() == list.Count) return true;
            Issue("InvalidSlotIndex", section, "栏位超出范围或重复。"); return false;
        }
        if (value.Weapons.Any(x => x is null) || value.Skills.Any(x => x is null) || value.Consumables.Any(x => x is null))
        { Issue("InvalidLoadout", "Formation", "编队栏位不能为 null。"); return result; }
        var validWeapons = SlotsValid(value.Weapons.Select(s => s.SlotIndex), WeaponRules.SlotCount, "Weapons");
        var validSkills = SlotsValid(value.Skills.Select(s => s.SlotIndex), SkillRules.SlotCount, "Skills");
        var validPotions = SlotsValid(value.Consumables.Select(s => s.SlotIndex), ConsumableRules.TotalSlotCount, "Consumables");
        var profession = skills.BaseProfessions.FirstOrDefault(x => string.Equals(x.Code, value.ProfessionCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profession is null) Issue("InvalidProfession", "Profession", "请选择有效的职业。");
        var levels = new Dictionary<string, int>(data.Levels, StringComparer.OrdinalIgnoreCase);
        levels[character.ProfessionCode] = character.Level;
        var actor = JsonSerializer.Deserialize<Character>(JsonSerializer.Serialize(character))!;
        actor.ProfessionCode = profession?.Code ?? character.ProfessionCode;
        actor.AdvancedProfessionCode = null;
        actor.Level = levels.GetValueOrDefault(actor.ProfessionCode, 1);
        actor.BattleMaxHpLimit = null;
        BattleConsumableBonusCalculator.Apply(actor, null);
        result.ProfessionName = profession?.Name ?? value.ProfessionCode ?? "";
        result.Level = actor.Level;
        if (profession is not null)
        {
            var available = skills.NativeSkills(actor).Concat(skills.AvailableSharedSkills(actor, levels)
                .Where(s => skills.Resolve(actor, s.Code, levels) is not null));
            result.AvailableSkills = available.Select(s => new LearnedSkillResponse
            {
                Code = s.Code, Name = s.Name, Description = _information.Description(s),
                EffectType = SkillCatalog.PrimaryEffectType(s), Power = SkillCatalog.PrimaryPower(s),
                Level = s.IsShared ? 3 : SkillCatalog.RankFor(s, actor.Level), IsShared = s.IsShared,
                SourceProfessionCode = s.ProfessionCode, AutoCondition = SkillCatalog.AutoConditionFor(s),
                UnlockLevel = s.UnlockLevel, Level2UnlockLevel = s.Level2UnlockLevel, Level3UnlockLevel = s.Level3UnlockLevel,
                CooldownRounds = s.CooldownRounds, InitialCooldownRounds = s.InitialCooldownRounds, Effects = _information.Effects(s)
            }).ToList();
            result.SkillLibrary = BuildSkillLibrary(actor, levels);
        }
        if (validSkills)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var sharedCount = 0;
            foreach (var s in value.Skills)
            {
                if (s.AutoHpThresholdPercent is < 1 or > 100) Issue("InvalidHpThreshold", "Skills", "血量阈值必须在 1～100 之间。", s.SlotIndex);
                if (!SkillAutoRules.IsValidOverride(SkillAutoRules.Normalize(s.AutoConditionOverride))) Issue("InvalidAutoCondition", "Skills", "自动条件无效。", s.SlotIndex);
                if (s.SkillCode is null) continue;
                var definition = skills.Resolve(actor, s.SkillCode.Trim(), levels);
                if (definition is null) Issue("SkillNotLearned", "Skills", "该职业尚不能装备此技能。", s.SlotIndex);
                else
                {
                    if (!used.Add(definition.Code)) Issue("SkillAlreadyEquipped", "Skills", "同一技能不能重复装备。", s.SlotIndex);
                    if (definition.IsShared && ++sharedCount > 1) Issue("SharedSkillLimitReached", "Skills", "最多装备一个共享技能。", s.SlotIndex);
                }
            }
        }
        var inventory = data.Weapons;
        var selected = new List<CharacterWeapon>();
        if (validWeapons)
        {
            var used = new HashSet<int>();
            foreach (var s in value.Weapons.Where(s => s.WeaponId.HasValue))
            {
                var weapon = inventory.FirstOrDefault(w => w.Id == s.WeaponId);
                if (weapon is null) Issue("WeaponNotOwned", "Weapons", "武器不存在或不属于当前角色。", s.SlotIndex);
                else if (!used.Add(weapon.Id)) Issue("WeaponAlreadyEquipped", "Weapons", "同一把武器不能重复占位。", s.SlotIndex);
                else selected.Add(new CharacterWeapon { Id = weapon.Id, CharacterId = weapon.CharacterId, Element = weapon.Element,
                    Attack = weapon.Attack, MaxHp = weapon.MaxHp, Skills = weapon.Skills, EquippedSlotIndex = s.SlotIndex });
            }
            var main = selected.FirstOrDefault(w => w.EquippedSlotIndex == WeaponRules.MainSlotIndex);
            if (main is null) Issue("MainWeaponRequired", "Weapons", "出战需要配置主武器。", WeaponRules.MainSlotIndex);
            result.MainElement = main?.Element;
            if (main is not null)
            {
                var ownedMain = inventory.First(w => w.Id == main.Id);
                result.MainWeapon = new FormationMainWeaponResponse
                {
                    WeaponId = ownedMain.Id, Code = ownedMain.WeaponCode, Name = ownedMain.Name,
                    Element = ownedMain.Element, QualityRank = ownedMain.QualityRank
                };
            }
            if (main is not null && group.HasValue && main.Element != group.Value) Issue("GroupElementMismatch", "Weapons", "主武器属性与编队分类不同，战斗仍按实际主武器属性计算。", 1, true);
            var equipment = weapons.RecalculateEquipmentStats(actor, selected);
            result.WeaponAttack = equipment.Attack;
            result.WeaponMaxHp = equipment.MaxHp;
            result.WeaponAttackBonusPercent = equipment.Bonuses.AttackPercent;
            result.WeaponHealthBonusPercent = equipment.Bonuses.HealthPercent;
            result.WeaponCriticalChancePercent = equipment.Bonuses.CriticalChancePercent;
            result.WeaponSkills = equipment.Bonuses.ActiveSkills.Select(skill => new ActiveWeaponSkillResponse
            {
                SkillCode = skill.Code, Name = skill.Name, Level = skill.Level,
                TotalPercent = skill.TotalPercent, Description = skill.Description
            }).ToList();
            result.WeaponEffects = equipment.Bonuses.Effects.Select(effect => new WeaponEffectResponse
            {
                EffectType = effect.EffectType, Name = WeaponEffectLabels.Name(effect.EffectType),
                Description = WeaponEffectLabels.Description(effect.EffectType),
                EffectiveLevel = effect.EffectiveLevel, TotalPercent = effect.TotalPercent
            }).ToList();
            result.Attack = TalentRules.EffectiveAttack(actor); result.MaxHp = TalentRules.EffectiveMaxHp(actor);
        }
        if (validPotions)
        {
            var stacks = data.Stacks;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in value.Consumables)
            {
                if (s.AutoHpThresholdPercent is < 1 or > 100) Issue("InvalidHpThreshold", "Consumables", "血量阈值必须在 1～100 之间。", s.SlotIndex);
                if (!SkillAutoRules.IsValidOverride(SkillAutoRules.Normalize(s.AutoConditionOverride)))
                    Issue("InvalidAutoCondition", "Consumables", "自动条件无效。", s.SlotIndex);
                if (s.ItemCode is null) continue;
                var item = consumables.FindItem(s.ItemCode.Trim());
                if (item is null) Issue("UnknownConsumable", "Consumables", "该补给已不存在。", s.SlotIndex);
                else
                {
                    if (!ConsumableRules.CanEquip(s.SlotIndex, item.Kind)) Issue("WrongConsumableSlot", "Consumables", "补给类型与栏位不符。", s.SlotIndex);
                    if (!used.Add(item.Code)) Issue("ConsumableAlreadyEquipped", "Consumables", "同一补给不能重复装备。", s.SlotIndex);
                    if (stacks.GetValueOrDefault(item.Code) <= 0) Issue("ConsumableOutOfStock", "Consumables", "此补给库存为零，战斗中无法使用。", s.SlotIndex, true);
                }
            }
        }
        if (value.SoulAutoHpThresholdPercent is < 1 or > 100)
            Issue("InvalidHpThreshold", "SoulImprint", "血量阈值必须在 1～100 之间。");
        if (!SkillAutoRules.IsValidOverride(SkillAutoRules.Normalize(value.SoulAutoConditionOverride)))
            Issue("InvalidAutoCondition", "SoulImprint", "自动条件无效。");
        if (value.SoulImprintId is { } soulId)
        {
            var soul = data.Souls.GetValueOrDefault(soulId);
            if (soul is null) Issue("SoulImprintNotOwned", "SoulImprint", "魂印不存在或不属于当前角色。");
            else if (souls.Find(soul.SoulImprintCode) is null) Issue("SoulImprintUnavailable", "SoulImprint", "魂印定义已不存在。");
        }
        result.CanDeploy = result.Issues.All(i => i.Severity != "Error");
        return result;
    }

    public async Task<string?> ApplyAsync(Character character, CombatLoadoutDefinition value)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Loadout application requires a caller-owned transaction.");
        var preview = await PreviewAsync(character, value);
        if (!preview.CanDeploy) return preview.Issues.First(i => i.Severity == "Error").Code;
        if (await CharacterActivityManager.IsBusyAsync(db, character.Id)) return "CharacterBusy";
        var oldHp = character.Hp;
        var profession = skills.BaseProfessions.Single(p => string.Equals(p.Code, value.ProfessionCode.Trim(), StringComparison.OrdinalIgnoreCase));
        var skillSlots = await db.CharacterSkillSlots.Where(s => s.CharacterId == character.Id).ToListAsync();
        var progress = new CombatProfessionProgressStore(db);
        var current = await progress.CaptureActiveAsync(character);
        CombatSkillLoadoutCodec.EnsureSupportedVersion(current.SkillLoadoutJson);
        current.SkillLoadoutJson = CombatSkillLoadoutCodec.Capture(skillSlots);
        if (character.ProfessionCode != profession.Code) await progress.RestoreAsync(character, profession.Code);
        character.AdvancedProfessionCode = null;
        var levels = await progress.ReadLevelsAsync(character.Id); levels[character.ProfessionCode] = character.Level;
        var inventory = await db.CharacterWeapons.Include(w => w.Skills).Where(w => w.CharacterId == character.Id).ToListAsync();
        var imprints = await db.CharacterSoulImprints.Where(s => s.CharacterId == character.Id).ToListAsync();
        foreach (var w in inventory.Where(w => w.EquippedSlotIndex.HasValue)) { w.EquippedSlotIndex = null; w.Version++; }
        foreach (var s in imprints.Where(s => s.EquippedSlotIndex.HasValue)) { s.EquippedSlotIndex = null; s.Version++; }
        character.Version++;
        await db.SaveChangesAsync(); // Release unique slot keys; still inside the caller's transaction.
        foreach (var w in value.Weapons.Where(w => w.WeaponId.HasValue))
        {
            var item = inventory.Single(x => x.Id == w.WeaponId); item.EquippedSlotIndex = w.SlotIndex; item.Version++;
        }
        foreach (var index in Enumerable.Range(1, SkillRules.SlotCount))
        {
            var source = value.Skills.FirstOrDefault(s => s.SlotIndex == index);
            var slot = skillSlots.FirstOrDefault(s => s.SlotIndex == index);
            if (slot is null) { slot = new() { CharacterId = character.Id, SlotIndex = index }; db.CharacterSkillSlots.Add(slot); }
            var definition = skills.Resolve(character, source?.SkillCode?.Trim(), levels);
            slot.SkillCode = definition?.Code;
            slot.AutoUseEnabled = definition is not null && source!.AutoUseEnabled;
            slot.AutoConditionOverride = definition is null ? null : SkillAutoRules.Normalize(source!.AutoConditionOverride);
            slot.AutoHpThresholdPercent = source?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent;
            slot.Version++;
        }
        var potionSlots = await db.CharacterConsumableSlots.Where(s => s.CharacterId == character.Id).ToListAsync();
        foreach (var index in Enumerable.Range(1, ConsumableRules.TotalSlotCount))
        {
            var source = value.Consumables.FirstOrDefault(s => s.SlotIndex == index);
            var slot = potionSlots.FirstOrDefault(s => s.SlotIndex == index);
            if (slot is null) { slot = new() { CharacterId = character.Id, SlotIndex = index }; db.CharacterConsumableSlots.Add(slot); }
            var item = source?.ItemCode is null ? null : consumables.FindItem(source.ItemCode.Trim());
            slot.ItemCode = item?.Code; slot.AutoUseEnabled = item is { Kind: "Healing" or "CombatBuff" } && source!.AutoUseEnabled;
            slot.AutoConditionOverride = item is { Kind: "Healing" or "CombatBuff" } ? SkillAutoRules.Normalize(source!.AutoConditionOverride) : null;
            slot.AutoHpThresholdPercent = source?.AutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent; slot.Version++;
        }
        if (value.SoulImprintId is { } id)
        {
            var soul = imprints.Single(s => s.Id == id); soul.EquippedSlotIndex = SoulImprintRules.SlotIndex;
            soul.AutoUseEnabled = value.SoulAutoUseEnabled; soul.Version++;
            soul.AutoConditionOverride = SkillAutoRules.Normalize(value.SoulAutoConditionOverride);
            soul.AutoHpThresholdPercent = value.SoulAutoHpThresholdPercent;
        }
        weapons.RecalculateEquipmentStats(character, inventory);
        character.Hp = Math.Min(oldHp, TalentRules.EffectiveMaxHp(character));
        await StoryProgressService.RefreshAsync(db, character.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return null;
    }
}
