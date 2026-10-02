using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SurveyAnswers_ResponseId",
                table: "SurveyAnswers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Surveys_ClosesAfterOpens",
                table: "Surveys",
                sql: "\"OpensAt\" IS NULL OR \"ClosesAt\" IS NULL OR \"ClosesAt\" > \"OpensAt\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SurveyQuestions_ScaleRange",
                table: "SurveyQuestions",
                sql: "\"ScaleMin\" IS NULL OR \"ScaleMax\" IS NULL OR \"ScaleMin\" < \"ScaleMax\"");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyAnswers_ResponseId_QuestionId",
                table: "SurveyAnswers",
                columns: new[] { "ResponseId", "QuestionId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SurveyAnswers_SingleValue",
                table: "SurveyAnswers",
                sql: "num_nonnulls(\"IntValue\", \"OptionId\", \"TextValue\") <= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Periods_EndNotBeforeStart",
                table: "Periods",
                sql: "\"StartDate\" IS NULL OR \"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Groups_EndNotBeforeStart",
                table: "Groups",
                sql: "\"StartDate\" IS NULL OR \"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");

            // The system serves exactly one MBA programme. An index on a constant cannot be expressed in the EF model.
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_Programs_Single\" ON \"Programs\" ((1));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_Programs_Single\";");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Surveys_ClosesAfterOpens",
                table: "Surveys");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SurveyQuestions_ScaleRange",
                table: "SurveyQuestions");

            migrationBuilder.DropIndex(
                name: "IX_SurveyAnswers_ResponseId_QuestionId",
                table: "SurveyAnswers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SurveyAnswers_SingleValue",
                table: "SurveyAnswers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Periods_EndNotBeforeStart",
                table: "Periods");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Groups_EndNotBeforeStart",
                table: "Groups");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyAnswers_ResponseId",
                table: "SurveyAnswers",
                column: "ResponseId");
        }
    }
}
