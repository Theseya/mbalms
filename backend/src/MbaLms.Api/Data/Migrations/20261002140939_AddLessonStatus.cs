using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Lessons_TeacherId",
                table: "Lessons");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Lessons",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Scheduled");

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_TeacherId_StartsAt",
                table: "Lessons",
                columns: new[] { "TeacherId", "StartsAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lessons_Status",
                table: "Lessons",
                sql: "\"Status\" IN ('Scheduled', 'Cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Lessons_TeacherId_StartsAt",
                table: "Lessons");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lessons_Status",
                table: "Lessons");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Lessons");

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_TeacherId",
                table: "Lessons",
                column: "TeacherId");
        }
    }
}
