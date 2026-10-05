using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveyScaleLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScaleMaxLabel",
                table: "SurveyQuestions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScaleMinLabel",
                table: "SurveyQuestions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScaleMaxLabel",
                table: "SurveyQuestions");

            migrationBuilder.DropColumn(
                name: "ScaleMinLabel",
                table: "SurveyQuestions");
        }
    }
}
