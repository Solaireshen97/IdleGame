using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;

namespace Game.Client.Services;

public static class BattleStatisticsPresentation
{
    public static string ActorName(BattleStatisticsActorResponse actor) =>
        string.IsNullOrWhiteSpace(actor.Name) ? $"角色 #{actor.CharacterId}" : actor.Name;
    public static string Profession(string code) => code switch
    {
        "swordsman" => "骑士", "acolyte" => "祭司", "mage" => "法师",
        "hunter" => "猎人", "rogue" => "盗贼", _ => "职业信息未记录"
    };
    public static string Element(ElementType? element) => element is { } value ? WeaponRules.ElementName(value) + "属性" : "属性未记录";
    public static string Action(BattleActionKind kind) => kind switch
    {
        BattleActionKind.Skill => "技能", BattleActionKind.NormalAttack => "普攻", BattleActionKind.SoulImprint => "魂印",
        BattleActionKind.Counter => "反击", BattleActionKind.FollowUp => "追击", BattleActionKind.Periodic => "持续效果",
        BattleActionKind.Mechanic => "机制", BattleActionKind.Consumable => "消耗品", _ => "其他效果"
    };
    public static string Ability(BattleStatisticsAbilityResponse ability) => string.IsNullOrWhiteSpace(ability.Label) ? "其他效果" : ability.Label;
}
