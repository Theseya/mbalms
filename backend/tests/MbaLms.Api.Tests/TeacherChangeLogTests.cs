using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MbaLms.Api.Tests;

public class TeacherChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Teacher_crud_fio_email_markers_no_op_and_delete()
    {
        var manager = await ManagerAsync();
        var secret = $"{Unique("secret")}@test.local";

        var empty = await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = Unique("CL-T"), firstName = "Иван", middleName = (string?)null, email = (string?)null },
            HttpStatusCode.Created);
        var emptyLog = Assert.Single(await LogsFor(manager, ChangeLogEntityType.Teacher, empty.Id));
        Assert.Equal(ChangeLogAction.Created, emptyLog.Action);
        Assert.Equal(ApiFactory.ManagerEmail, emptyLog.ActorEmail);
        Assert.Equal(ChangeLogEntityType.Teacher, emptyLog.EntityType);
        Assert.True(emptyLog.EntityExists);
        Assert.Equal(empty.FullName, emptyLog.EntityTitle);
        Assert.DoesNotContain(emptyLog.Changes, c => c.Field == "email");
        Assert.DoesNotContain(emptyLog.Changes, c => c.Field == "middleName");

        var withEmail = await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = Unique("CL-T2"), firstName = "Мария", middleName = "Ивановна", email = secret },
            HttpStatusCode.Created);
        var createLog = Assert.Single(await LogsFor(manager, ChangeLogEntityType.Teacher, withEmail.Id));
        Assert.Contains(createLog.Changes, c => c.Field == "email" && c.New?.GetString() == "set");
        Assert.Contains(createLog.Changes, c => c.Field == "middleName" && c.New?.GetString() == "Ивановна");
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(createLog));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(createLog.Changes));

        await manager.PutJsonAsync<TeacherDto>($"/api/manager/teachers/{withEmail.Id}",
            new { lastName = withEmail.LastName, firstName = withEmail.FirstName, middleName = "Ивановна", email = secret });
        Assert.Single(await LogsFor(manager, ChangeLogEntityType.Teacher, withEmail.Id));

        var changedEmail = $"{Unique("chg")}@test.local";
        await manager.PutJsonAsync<TeacherDto>($"/api/manager/teachers/{withEmail.Id}",
            new { lastName = withEmail.LastName, firstName = withEmail.FirstName, middleName = "Ивановна", email = changedEmail });
        var afterEmail = await LogsFor(manager, ChangeLogEntityType.Teacher, withEmail.Id);
        Assert.Equal(2, afterEmail.Count);
        Assert.Contains(afterEmail[1].Changes, c => c.Field == "email" && c.New?.GetString() == "changed");
        Assert.DoesNotContain(changedEmail, JsonSerializer.Serialize(afterEmail[1]));

        var renamedLast = Unique("Renamed");
        await manager.PutJsonAsync<TeacherDto>($"/api/manager/teachers/{withEmail.Id}",
            new { lastName = renamedLast, firstName = "Мария", middleName = (string?)null, email = changedEmail });
        var afterFio = await LogsFor(manager, ChangeLogEntityType.Teacher, withEmail.Id);
        Assert.Equal(3, afterFio.Count);
        Assert.Contains(afterFio[2].Changes, c => c.Field == "lastName");
        Assert.Contains(afterFio[2].Changes, c => c.Field == "middleName");

        await manager.PutJsonAsync<TeacherDto>($"/api/manager/teachers/{withEmail.Id}",
            new { lastName = renamedLast, firstName = "Мария", middleName = (string?)null, email = (string?)null });
        var cleared = await LogsFor(manager, ChangeLogEntityType.Teacher, withEmail.Id);
        Assert.Equal(4, cleared.Count);
        Assert.Contains(cleared[3].Changes, c => c.Field == "email" && c.New?.GetString() == "cleared");

        var titleBeforeDelete = cleared[^1].EntityTitle;
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/teachers/{withEmail.Id}")).StatusCode);
        var deleted = await LogsFor(manager, ChangeLogEntityType.Teacher, withEmail.Id);
        Assert.Equal(5, deleted.Count);
        Assert.Equal(ChangeLogAction.Deleted, deleted[^1].Action);
        Assert.False(deleted[^1].EntityExists);
        Assert.Equal(titleBeforeDelete, deleted[^1].EntityTitle);
        Assert.DoesNotContain(changedEmail, JsonSerializer.Serialize(deleted[^1]));
    }

    [Fact]
    public async Task Teacher_in_use_delete_does_not_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var teacher = await CreateTeacherAsync(manager);
        var logsBefore = await CountFor(manager, ChangeLogEntityType.Teacher, teacher.Id);
        await CreateLessonAsync(manager, group.Id, teacherId: teacher.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/manager/teachers/{teacher.Id}")).StatusCode);
        Assert.Equal(logsBefore, await CountFor(manager, ChangeLogEntityType.Teacher, teacher.Id));
    }

    [Fact]
    public async Task Student_cannot_read_teacher_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.GetAsync("/api/manager/change-log?entityType=Teacher&page=1&pageSize=10")).StatusCode);
    }

    [Fact]
    public async Task Failed_SaveChanges_on_teacher_update_rolls_back_teacher_and_change_log()
    {
        var manager = await ManagerAsync();
        var teacher = await CreateTeacherAsync(manager);
        var originalFirst = teacher.FirstName;
        var logCountBefore = await CountForDb(teacher.Id);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync($"/api/manager/teachers/{teacher.Id}", new
            {
                lastName = teacher.LastName,
                firstName = Unique("ShouldNotPersist"),
                middleName = (string?)null,
                email = $"{Unique("x")}@test.local"
            });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        var persisted = await db.Teachers.AsNoTracking().SingleAsync(t => t.Id == teacher.Id);
        Assert.Equal(originalFirst, persisted.FirstName);
        Assert.Equal(logCountBefore, await db.ChangeLogEntries.CountAsync(e => e.EntityId == teacher.Id));
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == teacher.Id && e.Action == ChangeLogAction.Updated));
    }

    [Fact]
    public async Task Failed_SaveChanges_on_teacher_create_rolls_back()
    {
        var manager = await ManagerAsync();
        var logsBefore = await TotalLogsDb();
        var lastName = Unique("AtomicTeacher");

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/teachers",
                new { lastName, firstName = "Test", email = $"{Unique("a")}@test.local" });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.False(await db.Teachers.AnyAsync(t => t.LastName == lastName));
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
    }

    private static async Task<List<ChangeLogListItemDto>> LogsFor(ApiClient manager, ChangeLogEntityType type, Guid entityId)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            $"/api/manager/change-log?entityType={type}&page=1&pageSize=100");
        return page.Items.Where(i => i.EntityId == entityId).OrderBy(i => i.OccurredAt).ThenBy(i => i.Id).ToList();
    }

    private static async Task<int> CountFor(ApiClient manager, ChangeLogEntityType type, Guid entityId) =>
        (await LogsFor(manager, type, entityId)).Count;

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Factory.ConnectionString).Options);

    private async Task<int> CountForDb(Guid entityId)
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync(e => e.EntityId == entityId);
    }

    private async Task<int> TotalLogsDb()
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync();
    }

    private async Task<LessonDto> CreateLessonAsync(ApiClient manager, Guid groupId, Guid teacherId)
    {
        var discipline = await CreateDisciplineAsync(manager);
        return await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", new
        {
            groupId,
            disciplineId = discipline.Id,
            teacherId,
            startsAt = "2030-03-10T10:00",
            endsAt = "2030-03-10T11:30",
            format = "Online",
            location = "https://meet.example/abc"
        }, HttpStatusCode.Created);
    }
}
