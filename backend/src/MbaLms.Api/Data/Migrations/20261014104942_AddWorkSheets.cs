using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkSheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssessmentElements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisciplineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentElements", x => x.Id);
                    table.CheckConstraint("CK_AssessmentElements_WeightRange", "\"Weight\" >= 0 AND \"Weight\" <= 100");
                    table.ForeignKey(
                        name: "FK_AssessmentElements_Disciplines_DisciplineId",
                        column: x => x.DisciplineId,
                        principalTable: "Disciplines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DisciplineAssessmentSettings",
                columns: table => new
                {
                    DisciplineId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttendanceThreshold = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisciplineAssessmentSettings", x => x.DisciplineId);
                    table.CheckConstraint("CK_DisciplineAssessmentSettings_ThresholdRange", "\"AttendanceThreshold\" >= 0 AND \"AttendanceThreshold\" <= 100");
                    table.ForeignKey(
                        name: "FK_DisciplineAssessmentSettings_Disciplines_DisciplineId",
                        column: x => x.DisciplineId,
                        principalTable: "Disciplines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkSheets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisciplineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalLessons = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSheets", x => x.Id);
                    table.CheckConstraint("CK_WorkSheets_TotalLessons", "\"TotalLessons\" >= 0");
                    table.ForeignKey(
                        name: "FK_WorkSheets_Disciplines_DisciplineId",
                        column: x => x.DisciplineId,
                        principalTable: "Disciplines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkSheets_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkSheets_Periods_PeriodId",
                        column: x => x.PeriodId,
                        principalTable: "Periods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkSheetRows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkSheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttendedLessons = table.Column<int>(type: "integer", nullable: true),
                    CalculatedRaw = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    CalculatedRounded = table.Column<int>(type: "integer", nullable: true),
                    ManualFinal = table.Column<int>(type: "integer", nullable: true),
                    TransferredFinal = table.Column<int>(type: "integer", nullable: true),
                    TransferredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSheetRows", x => x.Id);
                    table.CheckConstraint("CK_WorkSheetRows_Attended", "\"AttendedLessons\" >= 0");
                    table.CheckConstraint("CK_WorkSheetRows_CalculatedRounded", "\"CalculatedRounded\" >= 0 AND \"CalculatedRounded\" <= 10");
                    table.CheckConstraint("CK_WorkSheetRows_ManualFinal", "\"ManualFinal\" >= 0 AND \"ManualFinal\" <= 10");
                    table.ForeignKey(
                        name: "FK_WorkSheetRows_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkSheetRows_WorkSheets_WorkSheetId",
                        column: x => x.WorkSheetId,
                        principalTable: "WorkSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkSheetScores",
                columns: table => new
                {
                    RowId = table.Column<Guid>(type: "uuid", nullable: false),
                    ElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSheetScores", x => new { x.RowId, x.ElementId });
                    table.CheckConstraint("CK_WorkSheetScores_ValueRange", "\"Value\" >= 0 AND \"Value\" <= 10");
                    table.ForeignKey(
                        name: "FK_WorkSheetScores_AssessmentElements_ElementId",
                        column: x => x.ElementId,
                        principalTable: "AssessmentElements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkSheetScores_WorkSheetRows_RowId",
                        column: x => x.RowId,
                        principalTable: "WorkSheetRows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentElements_DisciplineId_Order",
                table: "AssessmentElements",
                columns: new[] { "DisciplineId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSheetRows_StudentId",
                table: "WorkSheetRows",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSheetRows_WorkSheetId_StudentId",
                table: "WorkSheetRows",
                columns: new[] { "WorkSheetId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkSheets_DisciplineId",
                table: "WorkSheets",
                column: "DisciplineId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSheets_GroupId_DisciplineId_PeriodId",
                table: "WorkSheets",
                columns: new[] { "GroupId", "DisciplineId", "PeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkSheets_PeriodId",
                table: "WorkSheets",
                column: "PeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSheetScores_ElementId",
                table: "WorkSheetScores",
                column: "ElementId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DisciplineAssessmentSettings");

            migrationBuilder.DropTable(
                name: "WorkSheetScores");

            migrationBuilder.DropTable(
                name: "AssessmentElements");

            migrationBuilder.DropTable(
                name: "WorkSheetRows");

            migrationBuilder.DropTable(
                name: "WorkSheets");
        }
    }
}
