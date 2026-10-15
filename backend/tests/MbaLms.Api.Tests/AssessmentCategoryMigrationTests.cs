using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace MbaLms.Api.Tests;

public class AssessmentCategoryMigrationTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Wrap_keeps_element_ids_scores_totals_and_does_not_touch_grades()
    {
        var builder = new NpgsqlConnectionStringBuilder(Factory.ConnectionString) { Database = $"wrap_{Guid.NewGuid():N}" };
        await using (var admin = new NpgsqlConnection(Factory.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{builder.Database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(builder.ConnectionString).Options);

            await db.GetService<IMigrator>().MigrateAsync("20261014104942_AddWorkSheets");

            var programId = Guid.CreateVersion7();
            var userId = Guid.CreateVersion7();
            var groupId = Guid.CreateVersion7();
            var studentId = Guid.CreateVersion7();
            var disciplineId = Guid.CreateVersion7();
            var periodId = Guid.CreateVersion7();
            var caseId = Guid.CreateVersion7();
            var examId = Guid.CreateVersion7();
            var attendanceId = Guid.CreateVersion7();
            var sheetId = Guid.CreateVersion7();
            var rowId = Guid.CreateVersion7();
            var gradeId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;

            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Programs" ("Id", "Name") VALUES ({0}, {1});
                INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                    "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumberConfirmed",
                    "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ({2}, {3}, {4}, {3}, {4}, FALSE, NULL, {5}, {5}, FALSE, FALSE, TRUE, 0);
                INSERT INTO "Groups" ("Id", "ProgramId", "Name", "Status") VALUES ({6}, {0}, {7}, 'Active');
                INSERT INTO "Students" ("Id", "UserId", "LastName", "FirstName", "Email", "GroupId")
                VALUES ({8}, {2}, {9}, {10}, {3}, {6});
                INSERT INTO "Disciplines" ("Id", "Name") VALUES ({11}, {12});
                INSERT INTO "Periods" ("Id", "Name") VALUES ({13}, {14});
                INSERT INTO "AssessmentElements" ("Id", "DisciplineId", "Name", "Weight", "Order") VALUES
                    ({15}, {11}, {16}, 40, 0),
                    ({17}, {11}, {18}, 60, 1),
                    ({19}, {11}, {20}, 0, 2);
                INSERT INTO "WorkSheets" ("Id", "GroupId", "DisciplineId", "PeriodId", "TotalLessons", "UpdatedAt")
                VALUES ({21}, {6}, {11}, {13}, 10, {22});
                INSERT INTO "WorkSheetRows" ("Id", "WorkSheetId", "StudentId", "AttendedLessons", "CalculatedRaw",
                    "CalculatedRounded", "ManualFinal", "TransferredFinal", "TransferredAt", "UpdatedAt")
                VALUES ({23}, {21}, {8}, 8, 7.6, 8, 9, 8, {22}, {22});
                INSERT INTO "WorkSheetScores" ("RowId", "ElementId", "Value") VALUES
                    ({23}, {15}, 7),
                    ({23}, {17}, 8),
                    ({23}, {19}, 5);
                INSERT INTO "Grades" ("Id", "StudentId", "DisciplineId", "PeriodId", "Value", "Status", "UpdatedAt")
                VALUES ({24}, {8}, {11}, {13}, 80, 'Draft', {22});
                """,
                programId, "MBA", userId, "wrap@test.local", "WRAP@TEST.LOCAL", Guid.NewGuid().ToString(),
                groupId, "G-wrap", studentId, "Иванов", "Иван", disciplineId, "Финансы", periodId, "Семестр",
                caseId, "Кейс", examId, "Экзамен", attendanceId, "Посещаемость",
                sheetId, now, rowId, gradeId);

            var beforeScores = await db.Database.SqlQueryRaw<ScoreRow>("""
                SELECT "ElementId" AS "ElementId", "Value" AS "Value" FROM "WorkSheetScores" ORDER BY "ElementId"
                """).ToListAsync();
            Assert.Equal(3, beforeScores.Count);

            await db.Database.MigrateAsync();

            var afterScores = await db.WorkSheetScores.AsNoTracking().OrderBy(s => s.ElementId).ToListAsync();
            Assert.Equal(beforeScores.Select(s => (s.ElementId, s.Value)), afterScores.Select(s => (s.ElementId, s.Value)));
            Assert.Equal(new[] { caseId, examId, attendanceId }.OrderBy(id => id), afterScores.Select(s => s.ElementId).OrderBy(id => id));

            var categories = await db.AssessmentCategories.AsNoTracking().Include(c => c.Works)
                .Where(c => c.DisciplineId == disciplineId).ToListAsync();
            var manuals = categories.Where(c => c.Kind == AssessmentCategoryKind.Manual).OrderBy(c => c.Order).ToList();
            var auto = Assert.Single(categories, c => c.Kind == AssessmentCategoryKind.Attendance);
            Assert.Equal(0, auto.Weight);
            Assert.Empty(auto.Works);
            Assert.Equal(3, manuals.Count);
            Assert.Equal(caseId, Assert.Single(manuals[0].Works).Id);
            Assert.Equal(examId, Assert.Single(manuals[1].Works).Id);
            Assert.Equal(attendanceId, Assert.Single(manuals[2].Works).Id);
            Assert.Equal(40, manuals[0].Weight);
            Assert.Equal(60, manuals[1].Weight);
            Assert.Equal(0, manuals[2].Weight);
            Assert.True(AssessmentCategoryNames.LooksLikeAttendanceName(manuals[2].Name));
            Assert.Equal(AssessmentCategoryKind.Manual, manuals[2].Kind);

            var row = await db.WorkSheetRows.AsNoTracking().Include(r => r.Scores).SingleAsync(r => r.Id == rowId);
            Assert.Equal(7.6m, row.CalculatedRaw);
            Assert.Equal(8, row.CalculatedRounded);
            Assert.Equal(9, row.ManualFinal);
            Assert.Equal(8, row.TransferredFinal);
            var calc = WorkSheetRules.Calculate(categories, row.Scores.ToDictionary(s => s.ElementId, s => s.Value),
                row.AttendedLessons, 10);
            Assert.Equal(CalculationState.Calculated, calc.State);
            Assert.Equal(7.6m, calc.Raw);
            Assert.Equal(8, calc.Rounded);

            var grade = await db.Grades.AsNoTracking().SingleAsync(g => g.Id == gradeId);
            Assert.Equal(80, grade.Value);
            Assert.Equal(GradeStatus.Draft, grade.Status);
            Assert.Equal(1, await db.Grades.CountAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(Factory.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{builder.Database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed record ScoreRow(Guid ElementId, decimal Value);
}
