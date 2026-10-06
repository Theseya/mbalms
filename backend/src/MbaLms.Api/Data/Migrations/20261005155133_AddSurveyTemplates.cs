using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveyTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SurveyTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SurveyTemplateQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ScaleMin = table.Column<int>(type: "integer", nullable: true),
                    ScaleMax = table.Column<int>(type: "integer", nullable: true),
                    ScaleMinLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ScaleMaxLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyTemplateQuestions", x => x.Id);
                    table.CheckConstraint("CK_SurveyTemplateQuestions_ScaleRange", "\"ScaleMin\" IS NULL OR \"ScaleMax\" IS NULL OR \"ScaleMin\" < \"ScaleMax\"");
                    table.ForeignKey(
                        name: "FK_SurveyTemplateQuestions_SurveyTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "SurveyTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SurveyTemplateOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyTemplateOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurveyTemplateOptions_SurveyTemplateQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "SurveyTemplateQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyTemplateOptions_QuestionId",
                table: "SurveyTemplateOptions",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyTemplateQuestions_TemplateId",
                table: "SurveyTemplateQuestions",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyTemplates_CreatedAt",
                table: "SurveyTemplates",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SurveyTemplateOptions");

            migrationBuilder.DropTable(
                name: "SurveyTemplateQuestions");

            migrationBuilder.DropTable(
                name: "SurveyTemplates");
        }
    }
}
