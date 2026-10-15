using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssessmentCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssessmentCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisciplineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentCategories", x => x.Id);
                    table.CheckConstraint("CK_AssessmentCategories_WeightRange", "\"Weight\" >= 0 AND \"Weight\" <= 100");
                    table.ForeignKey(
                        name: "FK_AssessmentCategories_Disciplines_DisciplineId",
                        column: x => x.DisciplineId,
                        principalTable: "Disciplines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentCategories_DisciplineId_Kind",
                table: "AssessmentCategories",
                columns: new[] { "DisciplineId", "Kind" },
                unique: true,
                filter: "\"Kind\" = 'Attendance'");

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentCategories_DisciplineId_Order",
                table: "AssessmentCategories",
                columns: new[] { "DisciplineId", "Order" });

            // One category per old element, same Id so WorkSheetScores.ElementId stays valid on the work row.
            migrationBuilder.Sql("""
                INSERT INTO "AssessmentCategories" ("Id", "DisciplineId", "Name", "Weight", "Kind", "Order")
                SELECT "Id", "DisciplineId", "Name", "Weight", 'Manual', "Order"
                FROM "AssessmentElements";
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "AssessmentElements",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AssessmentElements" SET "CategoryId" = "Id";
                """);

            migrationBuilder.Sql("""
                INSERT INTO "AssessmentCategories" ("Id", "DisciplineId", "Name", "Weight", "Kind", "Order")
                SELECT md5(d."Id"::text || '-attendance')::uuid, d."Id", 'Посещаемость', 0, 'Attendance',
                       COALESCE((SELECT MAX(c."Order") + 1 FROM "AssessmentCategories" c WHERE c."DisciplineId" = d."Id"), 0)
                FROM "Disciplines" d
                WHERE NOT EXISTS (
                    SELECT 1 FROM "AssessmentCategories" c
                    WHERE c."DisciplineId" = d."Id" AND c."Kind" = 'Attendance');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AssessmentElements_Disciplines_DisciplineId",
                table: "AssessmentElements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentElements_WeightRange",
                table: "AssessmentElements");

            migrationBuilder.DropIndex(
                name: "IX_AssessmentElements_DisciplineId_Order",
                table: "AssessmentElements");

            migrationBuilder.DropColumn(
                name: "Weight",
                table: "AssessmentElements");

            migrationBuilder.DropColumn(
                name: "DisciplineId",
                table: "AssessmentElements");

            migrationBuilder.AlterColumn<Guid>(
                name: "CategoryId",
                table: "AssessmentElements",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentElements_CategoryId_Order",
                table: "AssessmentElements",
                columns: new[] { "CategoryId", "Order" });

            migrationBuilder.AddForeignKey(
                name: "FK_AssessmentElements_AssessmentCategories_CategoryId",
                table: "AssessmentElements",
                column: "CategoryId",
                principalTable: "AssessmentCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DisciplineId",
                table: "AssessmentElements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Weight",
                table: "AssessmentElements",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AssessmentElements" e
                SET "DisciplineId" = c."DisciplineId", "Weight" = c."Weight"
                FROM "AssessmentCategories" c
                WHERE e."CategoryId" = c."Id";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AssessmentElements_AssessmentCategories_CategoryId",
                table: "AssessmentElements");

            migrationBuilder.DropIndex(
                name: "IX_AssessmentElements_CategoryId_Order",
                table: "AssessmentElements");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "AssessmentElements");

            migrationBuilder.DropTable(
                name: "AssessmentCategories");

            migrationBuilder.AlterColumn<Guid>(
                name: "DisciplineId",
                table: "AssessmentElements",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Weight",
                table: "AssessmentElements",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,2)",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentElements_WeightRange",
                table: "AssessmentElements",
                sql: "\"Weight\" >= 0 AND \"Weight\" <= 100");

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentElements_DisciplineId_Order",
                table: "AssessmentElements",
                columns: new[] { "DisciplineId", "Order" });

            migrationBuilder.AddForeignKey(
                name: "FK_AssessmentElements_Disciplines_DisciplineId",
                table: "AssessmentElements",
                column: "DisciplineId",
                principalTable: "Disciplines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
