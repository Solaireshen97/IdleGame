using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public static class InventoryItemProjection
{
    public static InventoryEntryDto WeaponEntry(CharacterWeapon item, WeaponCatalog catalog, bool inRoom,
        List<InventoryFormationReferenceDto>? references = null) => new()
    {
        Key = $"weapon:{item.Id}", AssetKind = InventoryKinds.Weapon, Code = item.WeaponCode, InstanceId = item.Id,
        Version = item.Version, Name = item.Name, Description = $"攻击 {item.Attack} · 生命 {item.MaxHp}",
        Category = InventoryCategories.Weapons, Quantity = 1, IsDefinitionKnown = catalog.FindItem(item.WeaponCode) is not null,
        Tier = WeaponRules.FragmentTier(item.ItemLevel), ItemLevel = item.ItemLevel, QualityRank = item.QualityRank,
        Attack = item.Attack, MaxHp = item.MaxHp,
        Element = item.Element, EquippedSlotIndex = item.EquippedSlotIndex, IsEquipped = item.EquippedSlotIndex.HasValue,
        IsLocked = item.IsLocked, FormationReferences = references ?? [],
        Actions = InventoryActionPolicy.Weapon(item, catalog, inRoom, references?.Count > 0),
        Tags = [WeaponRules.QualityName(item.QualityRank)],
        Usages = [new() { Label = "编队配置武器", Route = "formations?section=weapons" }]
    };

    public static InventoryEntryDto SoulEntry(CharacterSoulImprint item, SoulImprintCatalog catalog, bool inRoom,
        List<InventoryFormationReferenceDto>? references = null)
    {
        var definition = catalog.Find(item.SoulImprintCode);
        return new()
        {
            Key = $"soul:{item.Id}", AssetKind = InventoryKinds.SoulImprint, Code = item.SoulImprintCode, InstanceId = item.Id,
            Version = item.Version, Name = definition?.Name ?? item.SoulImprintCode,
            Description = definition?.Description ?? "物品定义暂不可用，实例已保留。",
            Category = definition is null ? InventoryCategories.Other : InventoryCategories.SoulImprints,
            Quantity = 1, IsDefinitionKnown = definition is not null, Tier = definition?.Tier, Element = definition?.Element,
            IsEquipped = item.EquippedSlotIndex.HasValue, EquippedSlotIndex = item.EquippedSlotIndex, IsLocked = item.IsLocked,
            FormationReferences = references ?? [], Actions = InventoryActionPolicy.Soul(item, catalog, inRoom, references?.Count > 0),
            Usages = definition is null ? [] : [new() { Label = "编队配置魂印", Route = "formations?section=soul" }]
        };
    }

    public static CharacterWeaponResponse Weapon(CharacterWeapon item, WeaponCatalog catalog,
        WeaponBreakthroughCatalog breakthrough, ElementType? mainElement) => new()
    {
        Id = item.Id, WeaponCode = item.WeaponCode, Name = item.Name, Element = item.Element, Attack = item.Attack,
        MaxHp = item.MaxHp, ItemLevel = item.ItemLevel, FragmentTier = WeaponRules.FragmentTier(item.ItemLevel),
        BreakthroughTier = breakthrough.FindForWeapon(item.ItemLevel)?.Tier, SellGold = item.SellGold, CanSell = true,
        Origin = item.Origin, DismantleFragments = WeaponCatalog.BaseDismantleReturn(item),
        DismantleReturnQuantity = catalog.DismantleReturn(item), CanDismantle = catalog.CanDismantle(item),
        QualityRank = item.QualityRank, QualityName = WeaponRules.QualityName(item.QualityRank),
        QualityCode = WeaponRules.QualityCode(item.QualityRank), IsLocked = item.IsLocked, EquippedSlotIndex = item.EquippedSlotIndex,
        LockedSkills = (catalog.FindItem(item.WeaponCode)?.Skills ?? [])
            .Where((grant, index) => grant.UnlockQualityRank > item.QualityRank && item.Skills.All(skill => skill.SlotIndex != index + 1))
            .Select(grant => new LockedWeaponSkillResponse { Name = catalog.FindSkill(grant.Code)?.Name ?? grant.Code,
                UnlockQualityRank = grant.UnlockQualityRank,
                Description = catalog.FindSkill(grant.Code) is { } definition ? catalog.DescribeSkill(definition, grant.Level) : "" }).ToList(),
        Skills = item.Skills.OrderBy(s => s.SlotIndex).Select(skill =>
        {
            var definition = catalog.FindSkill(skill.SkillCode);
            return new WeaponSkillResponse
            {
                SlotIndex = skill.SlotIndex, SkillCode = skill.SkillCode, Name = definition?.Name ?? skill.SkillCode,
                Level = skill.Level, BaseLevel = skill.BaseLevel, EnhancementLevel = skill.EnhancementLevel,
                MaximumEnhancementLevel = WeaponRules.EnhancementLimit(item.QualityRank, skill.BaseLevel),
                NextEnhancementCost = definition is not null && skill.EnhancementLevel < WeaponRules.EnhancementLimit(item.QualityRank, skill.BaseLevel)
                    ? catalog.EnhancementCost(skill.EnhancementLevel) : null,
                TotalPercent = definition is null ? 0 : catalog.CalculateSkillPercent(definition, skill.Level),
                Description = definition is null ? "未知技能" : catalog.DescribeSkill(definition, skill.Level),
                IsActive = definition is not null && item.EquippedSlotIndex.HasValue && item.Element == mainElement
            };
        }).ToList()
    };

    public static CharacterSoulImprintResponse Soul(CharacterSoulImprint item, SoulImprintCatalog catalog)
    {
        var d = catalog.Find(item.SoulImprintCode);
        return new()
        {
            Id = item.Id, Code = item.SoulImprintCode, Name = d?.Name ?? item.SoulImprintCode,
            Description = d?.Description ?? "物品定义暂不可用，实例已保留。", Tier = d?.Tier ?? 0,
            Element = d?.Element ?? default, EffectType = d?.EffectType ?? default, PowerPercent = d?.PowerPercent ?? 0,
            SecondaryPowerPercent = d?.SecondaryPowerPercent ?? 0, DurationRounds = d?.DurationRounds ?? 0,
            InitialCooldownRounds = d?.InitialCooldownRounds ?? 0, CooldownRounds = d?.CooldownRounds ?? 0,
            DismantleFragments = d?.DismantleFragments ?? 0, IsEquipped = item.EquippedSlotIndex == 1,
            AutoUseEnabled = item.AutoUseEnabled, DefaultAutoCondition = d?.AutoCondition ?? "Always",
            AutoCondition = item.AutoConditionOverride ?? d?.AutoCondition ?? "Always",
            AutoConditionOverride = item.AutoConditionOverride, AutoHpThresholdPercent = item.AutoHpThresholdPercent,
            IsLocked = item.IsLocked
        };
    }
}
