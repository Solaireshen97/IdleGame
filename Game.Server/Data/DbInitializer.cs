using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Game.Shared.Models;
using Game.Shared.Enums;
using Game.Shared;
using Game.Server.Services;

namespace Game.Server.Data;

public static class DbInitializer
{
    private const string InitialCreateMigrationId = "20260612043042_InitialCreate";
    private const string AddActiveCharacterMigrationId = "20260612044400_AddActiveCharacterId";

    public static async Task InitializeAsync(GameDbContext dbContext, WeaponCatalog? weaponCatalog = null)
    {
        await AdoptLegacyEnsureCreatedDatabaseAsync(dbContext);
        await dbContext.Database.MigrateAsync();
        await EnsureDefaultDungeonsAsync(dbContext);
        if (weaponCatalog is not null) await SynchronizeWeaponBonusesAsync(dbContext, weaponCatalog);
    }

    private static async Task SynchronizeWeaponBonusesAsync(GameDbContext dbContext, WeaponCatalog catalog)
    {
        var weaponsByCharacter = (await dbContext.CharacterWeapons.Include(weapon => weapon.Skills).ToListAsync())
            .GroupBy(weapon => weapon.CharacterId).ToDictionary(group => group.Key, group => group.AsEnumerable());
        var characters = await dbContext.Characters.ToListAsync();
        foreach (var character in characters)
        {
            var previous = (character.WeaponAttackBonusPercent, character.WeaponHealthBonusPercent,
                character.WeaponCriticalChancePercent, character.Hp);
            catalog.ApplyBonuses(character, weaponsByCharacter.GetValueOrDefault(character.Id) ?? []);
            character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
            if (previous != (character.WeaponAttackBonusPercent, character.WeaponHealthBonusPercent,
                    character.WeaponCriticalChancePercent, character.Hp)) character.Version++;
        }
        await dbContext.SaveChangesAsync();
    }

    public static async Task EnsureDefaultDungeonsAsync(GameDbContext dbContext)
    {
        var defaults = new[]
        {
            new Dungeon { Code = "northshire-wolves", Name = "北郡幼狼讨伐", RegionName = "艾尔文森林", DungeonKind = "Hunt", Description = "清理徘徊在北郡农场外的幼狼，熟悉战斗与属性规则。", MinimumLevel = 1, RecommendedLevel = 1, IsVisible = true, MonsterName = "北郡幼狼", MonsterElement = ElementType.Wind, MonsterMaxHp = 35, MonsterAttack = 5, MonsterDefense = 1, SlotCount = 5, SortOrder = 1 },
            new Dungeon { Code = "forest-spiders", Name = "林地毒蛛讨伐", RegionName = "艾尔文森林", DungeonKind = "Hunt", Description = "森林边缘出现了带有毒性的蜘蛛，留意它们持续生效的毒液。", MinimumLevel = 2, RecommendedLevel = 2, IsVisible = true, MonsterName = "林地毒蛛", MonsterElement = ElementType.Dark, MonsterMaxHp = 45, MonsterAttack = 6, MonsterDefense = 1, SlotCount = 5, SortOrder = 2 },
            new Dungeon { Code = "stone-tusk-boars", Name = "石牙野猪讨伐", RegionName = "艾尔文森林", DungeonKind = "Hunt", Description = "强壮的野猪会蓄力冲锋，准备好承受正面的猛烈撞击。", MinimumLevel = 3, RecommendedLevel = 3, IsVisible = true, MonsterName = "石牙野猪", MonsterElement = ElementType.Earth, MonsterMaxHp = 60, MonsterAttack = 8, MonsterDefense = 2, SlotCount = 5, SortOrder = 3 },
            new Dungeon { Code = "murloc-raiders", Name = "河岸鱼人讨伐", RegionName = "艾尔文森林", DungeonKind = "Hunt", Description = "击退侵入河岸的鱼人掠夺者，它们的水浪会威胁整支队伍。", MinimumLevel = 4, RecommendedLevel = 4, IsVisible = true, MonsterName = "鱼人掠夺者", MonsterElement = ElementType.Water, MonsterMaxHp = 70, MonsterAttack = 9, MonsterDefense = 2, SlotCount = 5, SortOrder = 4 },
            new Dungeon { Code = "kobold-miners", Name = "狗头人矿工讨伐", RegionName = "艾尔文森林", DungeonKind = "Hunt", Description = "阻止矿工继续盗采，它们的矿镐会破坏前排角色的防护。", MinimumLevel = 5, RecommendedLevel = 5, IsVisible = true, MonsterName = "狗头人矿工", MonsterElement = ElementType.Earth, MonsterMaxHp = 85, MonsterAttack = 10, MonsterDefense = 3, SlotCount = 5, SortOrder = 5 },
            new Dungeon { Code = "kobold-geomancers", Name = "狗头人地卜师讨伐", RegionName = "艾尔文森林", DungeonKind = "Hunt", Description = "地卜师以火焰和战吼强化攻势，及时驱散或打断能够降低风险。", MinimumLevel = 6, RecommendedLevel = 6, IsVisible = true, MonsterName = "狗头人地卜师", MonsterElement = ElementType.Fire, MonsterMaxHp = 100, MonsterAttack = 12, MonsterDefense = 3, SlotCount = 5, SortOrder = 6 },
            new Dungeon { Code = "riverpaw-gnolls", Name = "河爪豺狼人讨伐", RegionName = "艾尔文森林", DungeonKind = "Hunt", Description = "击败盘踞道路的河爪豺狼人，为进入矿洞前完成最后的准备。", MinimumLevel = 7, RecommendedLevel = 7, IsVisible = true, MonsterName = "河爪豺狼人", MonsterElement = ElementType.Wind, MonsterMaxHp = 120, MonsterAttack = 14, MonsterDefense = 4, SlotCount = 5, SortOrder = 7 },
            new Dungeon { Code = "kobold-mine", Name = "狗头人矿洞", RegionName = "艾尔文森林", DungeonKind = "Dungeon", Description = "深入四层矿道，击败矿工、地卜师与监工，最终挑战矿洞首领金牙。", MinimumLevel = 8, RecommendedLevel = 8, IsVisible = true, MonsterName = "金牙", MonsterElement = ElementType.Earth, MonsterMaxHp = 220, MonsterAttack = 18, MonsterDefense = 6, SlotCount = 5, SortOrder = 8 },

            new Dungeon { Code = "slime-field", Name = "史莱姆平原", RegionName = "旧版测试区域", DungeonKind = "Legacy", Description = "旧版测试副本。", MinimumLevel = 1, RecommendedLevel = 1, IsVisible = false, MonsterName = "Slime", MonsterElement = ElementType.Wind, MonsterMaxHp = 50, MonsterAttack = 8, MonsterDefense = 2, SlotCount = 5, SortOrder = 1001 },
            new Dungeon { Code = "goblin-camp", Name = "哥布林营地", RegionName = "旧版测试区域", DungeonKind = "Legacy", Description = "旧版测试副本。", MinimumLevel = 1, RecommendedLevel = 1, IsVisible = false, MonsterName = "Goblin", MonsterElement = ElementType.Earth, MonsterMaxHp = 80, MonsterAttack = 12, MonsterDefense = 4, SlotCount = 5, SortOrder = 1002 },
            new Dungeon { Code = "wolf-forest", Name = "狼群森林", RegionName = "旧版测试区域", DungeonKind = "Legacy", Description = "旧版测试副本。", MinimumLevel = 1, RecommendedLevel = 1, IsVisible = false, MonsterName = "Wolf", MonsterElement = ElementType.Water, MonsterMaxHp = 65, MonsterAttack = 15, MonsterDefense = 3, SlotCount = 5, SortOrder = 1003 }
        };

        var existing = await dbContext.Dungeons.ToDictionaryAsync(dungeon => dungeon.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in defaults)
        {
            if (!existing.TryGetValue(definition.Code, out var dungeon))
            {
                dbContext.Dungeons.Add(definition);
                continue;
            }

            dungeon.Name = definition.Name;
            dungeon.RegionName = definition.RegionName;
            dungeon.DungeonKind = definition.DungeonKind;
            dungeon.Description = definition.Description;
            dungeon.MinimumLevel = definition.MinimumLevel;
            dungeon.RecommendedLevel = definition.RecommendedLevel;
            dungeon.IsVisible = definition.IsVisible;
            dungeon.MonsterName = definition.MonsterName;
            dungeon.MonsterElement = definition.MonsterElement;
            dungeon.MonsterMaxHp = definition.MonsterMaxHp;
            dungeon.MonsterAttack = definition.MonsterAttack;
            dungeon.MonsterDefense = definition.MonsterDefense;
            dungeon.SlotCount = definition.SlotCount;
            dungeon.SortOrder = definition.SortOrder;
        }
        await dbContext.SaveChangesAsync();
    }

    private static async Task AdoptLegacyEnsureCreatedDatabaseAsync(GameDbContext dbContext)
    {
        var databaseCreator = dbContext.GetService<IRelationalDatabaseCreator>();
        if (!await databaseCreator.ExistsAsync())
        {
            return;
        }

        var historyRepository = dbContext.GetService<IHistoryRepository>();
        if (await historyRepository.ExistsAsync())
        {
            return;
        }

        if (!await TableExistsAsync(dbContext, "Users"))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(historyRepository.GetCreateScript());
        await InsertHistoryAsync(dbContext, historyRepository, InitialCreateMigrationId);

        if (await ColumnExistsAsync(dbContext, "Users", "ActiveCharacterId"))
        {
            await InsertHistoryAsync(dbContext, historyRepository, AddActiveCharacterMigrationId);
        }
    }

    private static Task InsertHistoryAsync(
        GameDbContext dbContext,
        IHistoryRepository historyRepository,
        string migrationId)
    {
        var historyRow = new HistoryRow(migrationId, "8.0.6");
        return dbContext.Database.ExecuteSqlRawAsync(historyRepository.GetInsertScript(historyRow));
    }

    private static async Task<bool> TableExistsAsync(GameDbContext dbContext, string tableName)
    {
        var count = await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {tableName}")
            .SingleAsync();

        return count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(GameDbContext dbContext, string tableName, string columnName)
    {
        var count = await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info({tableName}) WHERE name = {columnName}")
            .SingleAsync();

        return count > 0;
    }
}
