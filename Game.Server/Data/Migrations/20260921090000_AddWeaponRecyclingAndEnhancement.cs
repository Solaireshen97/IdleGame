using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921090000_AddWeaponRecyclingAndEnhancement")]
public partial class AddWeaponRecyclingAndEnhancement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "ItemLevel", table: "CharacterWeapons",
            type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "SellGold", table: "CharacterWeapons",
            type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "DismantleFragments", table: "CharacterWeapons",
            type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<bool>(name: "IsLocked", table: "CharacterWeapons",
            type: "INTEGER", nullable: false, defaultValue: false);

        migrationBuilder.AddColumn<int>(name: "BaseLevel", table: "CharacterWeaponSkills",
            type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "QualityBonusLevel", table: "CharacterWeaponSkills",
            type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "EnhancementLevel", table: "CharacterWeaponSkills",
            type: "INTEGER", nullable: false, defaultValue: 0);

        migrationBuilder.Sql("UPDATE CharacterWeaponSkills SET BaseLevel = Level;");
        migrationBuilder.Sql("""
            UPDATE CharacterWeapons SET
                ItemLevel = CASE WeaponCode
                    WHEN 'cinder-knife' THEN 5 WHEN 'light-scepter' THEN 5
                    WHEN 'tide-saber' THEN 8 WHEN 'gale-bow' THEN 8
                    WHEN 'stone-hammer' THEN 12 WHEN 'dusk-dagger' THEN 15
                    ELSE 1 END,
                SellGold = CASE WeaponCode
                    WHEN 'ember-blade' THEN 20 WHEN 'lumen-staff' THEN 20
                    WHEN 'cinder-knife' THEN 10 WHEN 'light-scepter' THEN 10
                    WHEN 'tide-saber' THEN 15 WHEN 'gale-bow' THEN 15
                    WHEN 'stone-hammer' THEN 22 WHEN 'dusk-dagger' THEN 28
                    ELSE 1 END,
                DismantleFragments = CASE WeaponCode
                    WHEN 'ember-blade' THEN 2 WHEN 'lumen-staff' THEN 2
                    WHEN 'tide-saber' THEN 2 WHEN 'gale-bow' THEN 2
                    WHEN 'stone-hammer' THEN 2 WHEN 'dusk-dagger' THEN 3
                    ELSE 1 END,
                IsLocked = CASE WHEN EquippedSlotIndex = 1 THEN 1 ELSE 0 END;
            """);

    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "EnhancementLevel", table: "CharacterWeaponSkills");
        migrationBuilder.DropColumn(name: "QualityBonusLevel", table: "CharacterWeaponSkills");
        migrationBuilder.DropColumn(name: "BaseLevel", table: "CharacterWeaponSkills");
        migrationBuilder.DropColumn(name: "IsLocked", table: "CharacterWeapons");
        migrationBuilder.DropColumn(name: "DismantleFragments", table: "CharacterWeapons");
        migrationBuilder.DropColumn(name: "SellGold", table: "CharacterWeapons");
        migrationBuilder.DropColumn(name: "ItemLevel", table: "CharacterWeapons");
    }
}
