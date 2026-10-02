namespace Game.Shared;

public static class SkillAutoRules
{
    public static IReadOnlyList<string> Conditions { get; } = Array.AsReadOnly(new[]
    {
        "Always", "SelfHpBelowThreshold", "AllyHpBelowThreshold", "FrontAllyHpBelowThreshold",
        "MonsterHpBelowThreshold", "AllyHasDebuff", "MonsterHasBuff", "InterruptibleIntent", "PreferInterrupt"
    });

    public static string? Normalize(string? condition) => string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();

    public static bool IsValidOverride(string? condition) => condition is null || Conditions.Contains(condition, StringComparer.Ordinal);

    public static bool UsesHpThreshold(string condition) => condition is
        "LowestHpBelowThreshold" or "SelfHpBelowThreshold" or "AllyHpBelowThreshold" or
        "FrontAllyHpBelowThreshold" or "MonsterHpBelowThreshold";

    public static string Label(string condition) => condition switch
    {
        "Always" => "就绪即用",
        "SelfHpBelowThreshold" => "自身血量 ≤ 阈值",
        "AllyHpBelowThreshold" => "任一队友血量 ≤ 阈值（含自己）",
        "FrontAllyHpBelowThreshold" => "前排队友血量 ≤ 阈值",
        "MonsterHpBelowThreshold" => "怪物血量 ≤ 阈值",
        "AllyHasDebuff" => "队友有可净化减益",
        "MonsterHasBuff" => "怪物有可驱散增益",
        "InterruptibleIntent" => "怪物正在准备可打断技能",
        "PreferInterrupt" => "有可打断技能时留给打断，否则就绪即用",
        "LowestHpBelowThreshold" => "技能目标血量 ≤ 阈值",
        _ => "技能默认"
    };

    public static string Describe(string condition, int threshold) => condition switch
    {
        "Always" => "就绪即用",
        "LowestHpBelowThreshold" => $"技能目标生命值 ≤ {threshold}%",
        "SelfHpBelowThreshold" => $"自身生命值 ≤ {threshold}%",
        "AllyHpBelowThreshold" => $"任一存活队友生命值 ≤ {threshold}%（含自己）",
        "FrontAllyHpBelowThreshold" => $"前排存活队友生命值 ≤ {threshold}%",
        "MonsterHpBelowThreshold" => $"怪物生命值 ≤ {threshold}%",
        _ => Label(condition)
    };
}
