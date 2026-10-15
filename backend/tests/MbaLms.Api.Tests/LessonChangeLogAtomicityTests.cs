using System.Net;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MbaLms.Api.Tests;

/// <summary>
/// Proves Lesson + ChangeLog share one SaveChanges transaction: a real Postgres failure mid-save
/// rolls back both. Not a validation error before SQL.
/// </summary>
public class LessonChangeLogAtomicityTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Failed_SaveChanges_on_update_rolls_back_lesson_and_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var lesson = await CreateLessonAsync(manager, group.Id, "2033-01-10T10:00", "2033-01-10T11:00");

        await using (var beforeDb = NewDb())
        {
            var row = await beforeDb.Lessons.AsNoTracking().SingleAsync(l => l.Id == lesson.Id);
            Assert.Equal("https://meet.example/abc", row.Location);
            Assert.Equal(1, await beforeDb.ChangeLogEntries.CountAsync(e => e.EntityId == lesson.Id));
        }

        var originalStarts = lesson.StartsAt;
        var originalEnds = lesson.EndsAt;
        var originalLocation = lesson.Location;
        var logCountBefore = await CountLogs(lesson.Id);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync($"/api/manager/lessons/{lesson.Id}", new
            {
                groupId = lesson.GroupId,
                disciplineId = lesson.DisciplineId,
                teacherId = lesson.TeacherId,
                startsAt = "2033-01-10T14:00",
                endsAt = "2033-01-10T15:30",
                format = lesson.Format,
                location = "SHOULD-NOT-PERSIST",
                comment = lesson.Comment,
                status = lesson.Status
            });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog,
            "Interceptor must see Lesson + ChangeLogEntry already staged before SQL.");
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure,
            "Failure must come from a database command during SaveChanges, not a pre-SQL throw.");
        Assert.Equal(PostgresErrorCodes.CheckViolation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        var persisted = await db.Lessons.AsNoTracking().SingleAsync(l => l.Id == lesson.Id);
        Assert.Equal(originalLocation, persisted.Location);
        Assert.Equal(originalStarts, persisted.StartsAt);
        Assert.Equal(originalEnds, persisted.EndsAt);
        Assert.DoesNotContain("SHOULD-NOT-PERSIST", persisted.Location ?? "");
        Assert.Equal(logCountBefore, await db.ChangeLogEntries.CountAsync(e => e.EntityId == lesson.Id));
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == lesson.Id && e.Action == ChangeLogAction.Updated));
    }

    [Fact]
    public async Task Failed_SaveChanges_on_create_rolls_back_lesson_and_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var teacher = await CreateTeacherAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);
        var logsBefore = await TotalLogs();

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/lessons", new
            {
                groupId = group.Id,
                disciplineId = discipline.Id,
                teacherId = teacher.Id,
                startsAt = "2033-02-01T10:00",
                endsAt = "2033-02-01T11:00",
                format = "Online",
                location = "atomic-create-should-not-exist",
                comment = (string?)null,
                status = "Scheduled"
            });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.CheckViolation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.False(await db.Lessons.AnyAsync(l => l.Location == "atomic-create-should-not-exist"));
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Factory.ConnectionString).Options);

    private async Task<int> CountLogs(Guid entityId)
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync(e => e.EntityId == entityId);
    }

    private async Task<int> TotalLogs()
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync();
    }
}
