using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalculationMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalculationMode",
                table: "DisciplineAssessmentSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Weighted");

            migrationBuilder.AddColumn<bool>(
                name: "IncludeAttendanceInAverage",
                table: "DisciplineAssessmentSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludeInAverage",
                table: "AssessmentCategories",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalculationMode",
                table: "DisciplineAssessmentSettings");

            migrationBuilder.DropColumn(
                name: "IncludeAttendanceInAverage",
                table: "DisciplineAssessmentSettings");

            migrationBuilder.DropColumn(
                name: "IncludeInAverage",
                table: "AssessmentCategories");
        }
    }
}
