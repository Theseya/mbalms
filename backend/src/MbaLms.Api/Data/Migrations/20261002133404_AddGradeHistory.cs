using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MbaLms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGradeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GradeHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisciplineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OldValue = table.Column<int>(type: "integer", nullable: true),
                    NewValue = table.Column<int>(type: "integer", nullable: true),
                    OldStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    NewStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GradeHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GradeHistory_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GradeHistory_ChangedByUserId",
                table: "GradeHistory",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GradeHistory_GradeId",
                table: "GradeHistory",
                column: "GradeId");

            migrationBuilder.CreateIndex(
                name: "IX_GradeHistory_StudentId_DisciplineId_PeriodId_ChangedAt",
                table: "GradeHistory",
                columns: new[] { "StudentId", "DisciplineId", "PeriodId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GradeHistory");
        }
    }
}
