using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923060000_RemoveCampWarehouse")]
public sealed class RemoveCampWarehouse : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("Gold", "Characters", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "_WarehouseRecipients" (
                "UserId" INTEGER NOT NULL PRIMARY KEY,
                "CharacterId" INTEGER NOT NULL
            );
            INSERT INTO "_WarehouseRecipients" ("UserId", "CharacterId")
            SELECT DISTINCT stock."UserId",
                COALESCE(
                    (SELECT character."Id" FROM "Characters" AS character
                     WHERE character."UserId" = stock."UserId"
                       AND character."Id" = owner."ActiveCharacterId" LIMIT 1),
                    (SELECT character."Id" FROM "Characters" AS character
                     WHERE character."UserId" = stock."UserId"
                     ORDER BY character."Id" LIMIT 1))
            FROM "Users" AS owner
            JOIN (SELECT "Id" AS "UserId" FROM "Users" WHERE "Gold" > 0
                  UNION SELECT "UserId" FROM "UserWarehouseStacks" WHERE "Quantity" > 0) AS stock
                ON stock."UserId" = owner."Id";

            UPDATE "Characters"
            SET "Gold" = "Gold" +
                (SELECT owner."Gold" FROM "Users" AS owner
                 JOIN "_WarehouseRecipients" AS recipient ON recipient."UserId" = owner."Id"
                 WHERE recipient."CharacterId" = "Characters"."Id")
            WHERE "Id" IN (SELECT "CharacterId" FROM "_WarehouseRecipients");

            INSERT OR IGNORE INTO "CharacterBattleMilestones"
                ("CharacterId", "Kind", "TargetCode", "Count", "FirstAtUtc", "LastAtUtc")
            SELECT COALESCE(
                    (SELECT character."Id" FROM "Characters" AS character
                     WHERE character."UserId" = clear."UserId"
                       AND character."Id" = owner."ActiveCharacterId" LIMIT 1),
                    (SELECT character."Id" FROM "Characters" AS character
                     WHERE character."UserId" = clear."UserId"
                     ORDER BY character."Id" LIMIT 1)),
                'DungeonClear', dungeon."Code", 1, clear."ClearedAtUtc", clear."ClearedAtUtc"
            FROM "UserDungeonClears" AS clear
            JOIN "Users" AS owner ON owner."Id" = clear."UserId"
            JOIN "Dungeons" AS dungeon ON dungeon."Id" = clear."DungeonId"
            WHERE EXISTS (SELECT 1 FROM "Characters" WHERE "UserId" = clear."UserId")
              AND NOT EXISTS (
                SELECT 1 FROM "CharacterBattleMilestones" AS milestone
                JOIN "Characters" AS character ON character."Id" = milestone."CharacterId"
                WHERE character."UserId" = clear."UserId"
                  AND milestone."Kind" = 'DungeonClear'
                  AND milestone."TargetCode" = dungeon."Code");

            CREATE TEMP TABLE "_WarehouseCapacityCheck" (
                "Valid" INTEGER NOT NULL CHECK ("Valid" = 1)
            );
            INSERT INTO "_WarehouseCapacityCheck" ("Valid")
            SELECT CASE WHEN bag."Quantity" <= 2147483647 - stock."Quantity" THEN 1 ELSE 0 END
            FROM "UserWarehouseStacks" AS stock
            JOIN "_WarehouseRecipients" AS recipient ON recipient."UserId" = stock."UserId"
            JOIN "CharacterItemStacks" AS bag
                ON bag."CharacterId" = recipient."CharacterId"
               AND bag."ItemCode" = stock."ItemCode";

            UPDATE "CharacterItemStacks"
            SET "Quantity" = "Quantity" +
                (SELECT stock."Quantity" FROM "UserWarehouseStacks" AS stock
                 JOIN "_WarehouseRecipients" AS recipient ON recipient."UserId" = stock."UserId"
                 WHERE recipient."CharacterId" = "CharacterItemStacks"."CharacterId"
                   AND stock."ItemCode" = "CharacterItemStacks"."ItemCode"),
                "Version" = "Version" + 1
            WHERE EXISTS (
                SELECT 1 FROM "UserWarehouseStacks" AS stock
                JOIN "_WarehouseRecipients" AS recipient ON recipient."UserId" = stock."UserId"
                WHERE recipient."CharacterId" = "CharacterItemStacks"."CharacterId"
                  AND stock."ItemCode" = "CharacterItemStacks"."ItemCode"
                  AND stock."Quantity" > 0);

            INSERT INTO "CharacterItemStacks" ("CharacterId", "ItemCode", "Quantity", "Version")
            SELECT recipient."CharacterId", stock."ItemCode", stock."Quantity", 0
            FROM "UserWarehouseStacks" AS stock
            JOIN "_WarehouseRecipients" AS recipient ON recipient."UserId" = stock."UserId"
            WHERE stock."Quantity" > 0
              AND NOT EXISTS (
                SELECT 1 FROM "CharacterItemStacks" AS bag
                WHERE bag."CharacterId" = recipient."CharacterId"
                  AND bag."ItemCode" = stock."ItemCode");

            DROP TABLE "_WarehouseCapacityCheck";
            DROP TABLE "_WarehouseRecipients";
            """);
        migrationBuilder.DropTable("WarehouseTransferRecords");
        migrationBuilder.DropTable("UserWarehouseStacks");
        migrationBuilder.Sql("ALTER TABLE \"Users\" DROP COLUMN \"Gold\";");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Shared warehouse items and account gold have been merged into character inventories and cannot be separated again.");
}
