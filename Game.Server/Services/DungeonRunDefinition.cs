using System.Text.Json.Serialization;
using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed class DungeonRunDefinition
{
    public int SchemaVersion { get; set; } = 1;
    [JsonIgnore] public string Revision { get; set; } = string.Empty;
    public string DungeonCode { get; set; } = string.Empty;
    public string DungeonKind { get; set; } = string.Empty;
    public int DepthLevel { get; set; }
    public decimal DirectDamageVariancePercent { get; set; }
    public DungeonRewardEligibility RewardEligibility { get; set; }
    public DungeonDepthDefinitionOptions? Depth { get; set; }
    public int[] PartyHpPercentages { get; set; } = [];
    public List<DungeonMonsterDefinition> Monsters { get; set; } = [];
    public MonsterCombatOptions Combat { get; set; } = new();
    public DungeonRewardRules Rewards { get; set; } = new();
    public List<DungeonSeedDrop> RareSeeds { get; set; } = [];
}

public sealed record DungeonSeedDrop(string SeedCode, decimal DropChancePercent, bool FirstClearGuaranteed = false);
public sealed record DungeonMonsterDefinition(int WaveNumber, int Position, string Name,
    ElementType Element, int MaxHp, int Attack, int Defense, string CombatProfileCode,
    string RewardProfileCode, bool IsBoss)
{
    public static DungeonMonsterDefinition Capture(Monster monster) => new(monster.WaveNumber,
        monster.Position, monster.Name, monster.Element, monster.BaseMaxHp > 0 ? monster.BaseMaxHp : monster.MaxHp,
        monster.Attack, monster.Defense, monster.CombatProfileCode, monster.RewardProfileCode, monster.IsBoss);
    public void Restore(Monster monster)
    {
        monster.Name = Name;
        monster.Element = Element;
        monster.Hp = monster.MaxHp = monster.BaseMaxHp = MaxHp;
        monster.Attack = Attack;
        monster.Defense = Defense;
        monster.CombatProfileCode = CombatProfileCode;
        monster.RewardProfileCode = RewardProfileCode;
        monster.IsBoss = IsBoss;
    }
}

public sealed class DungeonRewardRules
{
    public CoopDropBonusOptions CoopDropBonus { get; set; } = new();
    public Dictionary<string, RewardBundleOptions> Kills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RewardBundleOptions> Clears { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WeaponRewardSnapshot> Weapons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public WeaponRewardSnapshot? FirstHuntWeapon { get; set; }
}
