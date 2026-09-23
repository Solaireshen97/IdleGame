using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923080000_AddProfessionProgressionAndTalents")]
public sealed class AddProfessionProgressionAndTalents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("GatheringExperience", "Characters", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("AlchemyExperience", "Characters", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("GatheringTalentPoints", "Characters", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("AlchemyTalentPoints", "Characters", type: "INTEGER", nullable: false, defaultValue: 0);

        migrationBuilder.AddColumn<int>("ExtraYieldChancePercent", "GatheringTasks", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("ExtraYieldQuantity", "GatheringTasks", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("RareBonusChancePercent", "GatheringTasks", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("BonusMaterialCode", "GatheringTasks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("BonusQuantity", "GatheringTasks", type: "INTEGER", nullable: false, defaultValue: 0);

        migrationBuilder.AddColumn<int>("ExtraYieldChancePercent", "ProductionTasks", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("ExtraYieldQuantity", "ProductionTasks", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("IngredientSaveChancePercent", "ProductionTasks", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("SavedIngredientQuantity", "ProductionTasks", type: "INTEGER", nullable: false, defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "CharacterProfessionTalents",
            columns: table => new
            {
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                ProfessionCode = table.Column<string>(type: "TEXT", nullable: false),
                NodeCode = table.Column<string>(type: "TEXT", nullable: false),
                Rank = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterProfessionTalents", row => new { row.CharacterId, row.ProfessionCode, row.NodeCode });
                table.CheckConstraint("CK_CharacterProfessionTalents_Rank", "Rank > 0");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("CharacterProfessionTalents");
        migrationBuilder.DropColumn("GatheringExperience", "Characters");
        migrationBuilder.DropColumn("AlchemyExperience", "Characters");
        migrationBuilder.DropColumn("GatheringTalentPoints", "Characters");
        migrationBuilder.DropColumn("AlchemyTalentPoints", "Characters");
        migrationBuilder.DropColumn("ExtraYieldChancePercent", "GatheringTasks");
        migrationBuilder.DropColumn("ExtraYieldQuantity", "GatheringTasks");
        migrationBuilder.DropColumn("RareBonusChancePercent", "GatheringTasks");
        migrationBuilder.DropColumn("BonusMaterialCode", "GatheringTasks");
        migrationBuilder.DropColumn("BonusQuantity", "GatheringTasks");
        migrationBuilder.DropColumn("ExtraYieldChancePercent", "ProductionTasks");
        migrationBuilder.DropColumn("ExtraYieldQuantity", "ProductionTasks");
        migrationBuilder.DropColumn("IngredientSaveChancePercent", "ProductionTasks");
        migrationBuilder.DropColumn("SavedIngredientQuantity", "ProductionTasks");
    }
}
