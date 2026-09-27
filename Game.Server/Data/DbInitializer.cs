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

    public static async Task InitializeAsync(GameDbContext dbContext, WeaponCatalog? weaponCatalog = null,
        WorldCatalog? world = null, DungeonEncounterCatalog? encounters = null)
    {
        await AdoptLegacyEnsureCreatedDatabaseAsync(dbContext);
        await dbContext.Database.MigrateAsync();
        world ??= WorldCatalog.LoadDefault();
        await EnsureDefaultDungeonsAsync(dbContext, world);
        await SynchronizeRegionalMonstersAsync(dbContext, world, encounters);
        if (weaponCatalog is not null)
        {
            await SynchronizeWeaponTemplatesAsync(dbContext, weaponCatalog);
            await SynchronizeWeaponBonusesAsync(dbContext, weaponCatalog);
        }
    }

    private static async Task SynchronizeRegionalMonstersAsync(GameDbContext dbContext, WorldCatalog world,
        DungeonEncounterCatalog? encounters)
    {
        var elements = world.Regions.ToDictionary(region => region.Code, region => region.FeaturedElement);
        var definitions = world.Dungeons.Where(dungeon => dungeon.IsVisible && encounters?.HasDefinition(dungeon.Code) == true)
            .ToDictionary(dungeon => dungeon.Code, dungeon => encounters!.CreateMonsters(dungeon)
                .ToDictionary(monster => (monster.WaveNumber, monster.Position)));
        // Run at startup before room cycles begin. Include every saved wave and old single-monster rooms.
        var entries = await (from room in dbContext.Rooms
            join dungeon in dbContext.Dungeons on room.DungeonId equals dungeon.Id
            from monster in dbContext.Monsters
            where dungeon.IsVisible && (monster.RoomId == room.Id ||
                monster.RoomId == null && monster.Id == room.MonsterId)
            select new { Room = room, Monster = monster, Dungeon = dungeon }).ToListAsync();
        var changedRooms = new Dictionary<int, Room>();
        foreach (var entry in entries)
        {
            if (!elements.TryGetValue(entry.Dungeon.RegionCode, out var element)) continue;
            var changed = entry.Monster.Element != element;
            entry.Monster.Element = element;
            if (definitions.TryGetValue(entry.Dungeon.Code, out var encounterDefinitions))
            {
                // Old single-monster rooms use the dungeon summary monster, not the first new wave.
                var definition = entry.Monster.RoomId is null
                    ? encounterDefinitions.Values.FirstOrDefault(monster => monster.Name == entry.Dungeon.MonsterName)
                    : encounterDefinitions.GetValueOrDefault((entry.Monster.WaveNumber, entry.Monster.Position));
                var name = entry.Monster.RoomId is null ? entry.Dungeon.MonsterName :
                    definition?.Name;
                if (name is not null && entry.Monster.Name != name)
                {
                    entry.Monster.Name = name;
                    changed = true;
                }
                if (definition is not null)
                {
                    var rewardProfile = string.IsNullOrWhiteSpace(definition.RewardProfileCode)
                        ? entry.Dungeon.Code : definition.RewardProfileCode;
                    if (entry.Monster.RewardProfileCode != rewardProfile)
                    {
                        // Update future kills only. Recorded rewards retain their original rolled snapshots.
                        entry.Monster.RewardProfileCode = rewardProfile;
                        changed = true;
                    }
                }
            }
            if (changed) changedRooms.TryAdd(entry.Room.Id, entry.Room);
        }
        foreach (var room in changedRooms.Values) room.Version++;
        await dbContext.SaveChangesAsync();
    }

    private static async Task SynchronizeWeaponTemplatesAsync(GameDbContext dbContext, WeaponCatalog catalog)
    {
        var weapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills).ToListAsync();
        var changed = weapons.Where(catalog.NeedsTemplateUpdate).ToList();
        foreach (var weapon in weapons.Where(weapon => !catalog.NeedsTemplateUpdate(weapon)))
            catalog.SynchronizeName(weapon);
        if (changed.Count == 0)
        {
            await dbContext.SaveChangesAsync();
            return;
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        // Delete old skill rows first, then insert the rebased rows within one transaction.
        // Otherwise changing two skill codes can transiently violate the unique index.
        var replacementSkills = new Dictionary<int, List<CharacterWeaponSkill>>();
        foreach (var weapon in changed)
        {
            var oldSkills = weapon.Skills.ToList();
            catalog.ApplyTemplate(weapon);
            replacementSkills[weapon.Id] = weapon.Skills;
            weapon.Skills = [];
            dbContext.CharacterWeaponSkills.RemoveRange(oldSkills);
        }
        await dbContext.SaveChangesAsync();
        foreach (var weapon in changed) weapon.Skills = replacementSkills[weapon.Id];
        var characterIds = changed.Select(weapon => weapon.CharacterId).Distinct().ToList();
        foreach (var character in await dbContext.Characters.Where(character => characterIds.Contains(character.Id)).ToListAsync())
        {
            var owned = weapons.Where(weapon => weapon.CharacterId == character.Id).ToList();
            character.Attack = owned.Where(weapon => weapon.EquippedSlotIndex.HasValue).Sum(weapon => weapon.Attack);
            character.MaxHp = owned.Where(weapon => weapon.EquippedSlotIndex.HasValue).Sum(weapon => weapon.MaxHp);
            catalog.ApplyBonuses(character, owned);
            character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
            character.Version++;
        }
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task SynchronizeWeaponBonusesAsync(GameDbContext dbContext, WeaponCatalog catalog)
    {
        var weaponsByCharacter = (await dbContext.CharacterWeapons.Include(weapon => weapon.Skills).ToListAsync())
            .GroupBy(weapon => weapon.CharacterId).ToDictionary(group => group.Key, group => group.AsEnumerable());
        var characters = await dbContext.Characters.ToListAsync();
        foreach (var character in characters)
        {
            var previous = (character.WeaponAttackBonusPercent, character.WeaponHealthBonusPercent,
                character.WeaponCriticalChancePercent, character.WeaponStaminaPercent, character.WeaponEnmityPercent,
                character.WeaponDoubleAttackChancePercent, character.WeaponNormalEchoPercent, character.WeaponSkillDamagePercent, character.Hp);
            catalog.ApplyBonuses(character, weaponsByCharacter.GetValueOrDefault(character.Id) ?? []);
            character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
            if (previous != (character.WeaponAttackBonusPercent, character.WeaponHealthBonusPercent,
                    character.WeaponCriticalChancePercent, character.WeaponStaminaPercent, character.WeaponEnmityPercent,
                    character.WeaponDoubleAttackChancePercent, character.WeaponNormalEchoPercent, character.WeaponSkillDamagePercent, character.Hp)) character.Version++;
        }
        await dbContext.SaveChangesAsync();
    }

    public static async Task EnsureDefaultDungeonsAsync(GameDbContext dbContext, WorldCatalog? world = null)
    {
        var defaults = (world ?? WorldCatalog.LoadDefault()).Dungeons;

        var existing = await dbContext.Dungeons.ToDictionaryAsync(dungeon => dungeon.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in defaults)
        {
            if (!existing.TryGetValue(definition.Code, out var dungeon))
            {
                var created = new Dungeon();
                dbContext.Entry(created).CurrentValues.SetValues(definition);
                dbContext.Dungeons.Add(created);
                continue;
            }

            dungeon.Name = definition.Name;
            dungeon.RegionName = definition.RegionName;
            dungeon.RegionCode = definition.RegionCode;
            dungeon.DungeonKind = definition.DungeonKind;
            dungeon.PartyScalingProfileCode = definition.PartyScalingProfileCode;
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
