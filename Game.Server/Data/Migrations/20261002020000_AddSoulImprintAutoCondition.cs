using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20261002020000_AddSoulImprintAutoCondition")]
public sealed class AddSoulImprintAutoCondition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AutoConditionOverride", "CharacterSoulImprints", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("AutoHpThresholdPercent", "CharacterSoulImprints", type: "INTEGER", nullable: false, defaultValue: 70);
        migrationBuilder.AddColumn<string>("SoulAutoConditionOverride", "CharacterBattleFormations", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("SoulAutoHpThresholdPercent", "CharacterBattleFormations", type: "INTEGER", nullable: false, defaultValue: 70);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE CharacterBattleFormations DROP COLUMN SoulAutoHpThresholdPercent;");
        migrationBuilder.Sql("ALTER TABLE CharacterBattleFormations DROP COLUMN SoulAutoConditionOverride;");
        migrationBuilder.Sql("ALTER TABLE CharacterSoulImprints DROP COLUMN AutoHpThresholdPercent;");
        migrationBuilder.Sql("ALTER TABLE CharacterSoulImprints DROP COLUMN AutoConditionOverride;");
    }
}
