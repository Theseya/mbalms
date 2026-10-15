using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MbaLms.Api.Tests;

public class ReferenceChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Group_crud_archive_restore_and_no_ops()
    {
        var manager = await ManagerAsync();
        var created = await manager.PostJsonAsync<GroupDto>("/api/manager/groups",
            new { name = Unique("CL-G"), startDate = "2030-01-01", endDate = "2030-06-30" }, HttpStatusCode.Created);

        var afterCreate = await LogsFor(manager, ChangeLogEntityType.Group, created.Id);
        Assert.Single(afterCreate);
        Assert.Equal(ChangeLogAction.Created, afterCreate[0].Action);
        Assert.Equal(ApiFactory.ManagerEmail, afterCreate[0].ActorEmail);
        Assert.True(afterCreate[0].EntityExists);
        Assert.Contains(afterCreate[0].Changes, c => c.Field == "name" && c.New?.GetString() == created.Name);
        Assert.Contains(afterCreate[0].Changes, c => c.Field == "status" && c.New?.GetString() == "Active");

        await manager.PutJsonAsync<GroupDto>($"/api/manager/groups/{created.Id}",
            new { name = created.Name, startDate = "2030-01-01", endDate = "2030-06-30" });
        Assert.Single(await LogsFor(manager, ChangeLogEntityType.Group, created.Id));

        var renamed = await manager.PutJsonAsync<GroupDto>($"/api/manager/groups/{created.Id}",
            new { name = Unique("CL-G2"), startDate = "2030-02-01", endDate = "2030-07-01" });
        var afterUpdate = await LogsFor(manager, ChangeLogEntityType.Group, created.Id);
        Assert.Equal(2, afterUpdate.Count);
        Assert.Equal(ChangeLogAction.Updated, afterUpdate[1].Action);
        Assert.Contains(afterUpdate[1].Changes, c => c.Field == "name");
        Assert.Contains(afterUpdate[1].Changes, c => c.Field == "startDate");

        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{created.Id}/archive");
        var afterArchive = await LogsFor(manager, ChangeLogEntityType.Group, created.Id);
        Assert.Equal(3, afterArchive.Count);
        Assert.Equal(ChangeLogAction.Updated, afterArchive[2].Action);
        Assert.Contains(afterArchive[2].Changes, c =>
            c.Field == "status" && c.Old?.GetString() == "Active" && c.New?.GetString() == "Archived");

        var repeatArchive = await manager.PostAsync($"/api/manager/groups/{created.Id}/archive");
        Assert.Equal(HttpStatusCode.Conflict, repeatArchive.StatusCode);
        Assert.Equal(3, (await LogsFor(manager, ChangeLogEntityType.Group, created.Id)).Count);

        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{created.Id}/restore");
        var afterRestore = await LogsFor(manager, ChangeLogEntityType.Group, created.Id);
        Assert.Equal(4, afterRestore.Count);
        Assert.Contains(afterRestore[3].Changes, c =>
            c.Field == "status" && c.Old?.GetString() == "Archived" && c.New?.GetString() == "Active");

        var repeatRestore = await manager.PostAsync($"/api/manager/groups/{created.Id}/restore");
        Assert.Equal(HttpStatusCode.Conflict, repeatRestore.StatusCode);
        Assert.Equal(4, (await LogsFor(manager, ChangeLogEntityType.Group, created.Id)).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/groups/{created.Id}")).StatusCode);
        var afterDelete = await LogsFor(manager, ChangeLogEntityType.Group, created.Id);
        Assert.Equal(5, afterDelete.Count);
        Assert.Equal(ChangeLogAction.Deleted, afterDelete[^1].Action);
        Assert.False(afterDelete[^1].EntityExists);
        Assert.Equal(renamed.Name, afterDelete[^1].EntityTitle);
    }

    [Fact]
    public async Task Group_validation_and_in_use_delete_do_not_log()
    {
        var manager = await ManagerAsync();
        var before = await TotalCount(manager);
        var bad = await manager.PostAsync("/api/manager/groups",
            new { name = Unique("Bad"), startDate = "2030-06-01", endDate = "2030-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(before, await TotalCount(manager));

        var group = await CreateGroupAsync(manager);
        var logsBefore = await CountFor(manager, ChangeLogEntityType.Group, group.Id);
        await CreateStudentAsync(manager, group.Id);
        var del = await manager.DeleteAsync($"/api/manager/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Equal(logsBefore, await CountFor(manager, ChangeLogEntityType.Group, group.Id));
    }

    [Fact]
    public async Task Discipline_description_markers_without_full_text()
    {
        var manager = await ManagerAsync();
        var empty = await manager.PostJsonAsync<DisciplineDto>("/api/manager/disciplines",
            new { name = Unique("EmptyDesc"), description = (string?)null }, HttpStatusCode.Created);
        var emptyLog = Assert.Single(await LogsFor(manager, ChangeLogEntityType.Discipline, empty.Id));
        Assert.DoesNotContain(emptyLog.Changes, c => c.Field == "description");

        var withDesc = await manager.PostJsonAsync<DisciplineDto>("/api/manager/disciplines",
            new { name = Unique("WithDesc"), description = "Секретное описание дисциплины" }, HttpStatusCode.Created);
        var createLog = Assert.Single(await LogsFor(manager, ChangeLogEntityType.Discipline, withDesc.Id));
        Assert.Contains(createLog.Changes, c => c.Field == "description" && c.New?.GetString() == "set");
        Assert.DoesNotContain("Секретное", JsonSerializer.Serialize(createLog.Changes));

        await manager.PutJsonAsync<DisciplineDto>($"/api/manager/disciplines/{withDesc.Id}",
            new { name = withDesc.Name, description = "Другое секретное" });
        var updated = await LogsFor(manager, ChangeLogEntityType.Discipline, withDesc.Id);
        Assert.Equal(2, updated.Count);
        Assert.Contains(updated[1].Changes, c => c.Field == "description" && c.New?.GetString() == "changed");
        Assert.DoesNotContain("Другое", JsonSerializer.Serialize(updated[1].Changes));

        await manager.PutJsonAsync<DisciplineDto>($"/api/manager/disciplines/{withDesc.Id}",
            new { name = withDesc.Name, description = (string?)null });
        var cleared = await LogsFor(manager, ChangeLogEntityType.Discipline, withDesc.Id);
        Assert.Equal(3, cleared.Count);
        Assert.Contains(cleared[2].Changes, c => c.Field == "description" && c.New?.GetString() == "cleared");

        await manager.PutJsonAsync<DisciplineDto>($"/api/manager/disciplines/{withDesc.Id}",
            new { name = withDesc.Name, description = (string?)null });
        Assert.Equal(3, (await LogsFor(manager, ChangeLogEntityType.Discipline, withDesc.Id)).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/disciplines/{withDesc.Id}")).StatusCode);
        var deleted = await LogsFor(manager, ChangeLogEntityType.Discipline, withDesc.Id);
        Assert.Equal(ChangeLogAction.Deleted, deleted[^1].Action);
        Assert.False(deleted[^1].EntityExists);
        Assert.DoesNotContain("секрет", JsonSerializer.Serialize(deleted[^1].Changes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcademicPeriod_crud_and_no_op()
    {
        var manager = await ManagerAsync();
        var created = await manager.PostJsonAsync<PeriodDto>("/api/manager/periods",
            new { name = Unique("CL-P"), startDate = "2031-01-01", endDate = "2031-06-30" }, HttpStatusCode.Created);
        var afterCreate = Assert.Single(await LogsFor(manager, ChangeLogEntityType.AcademicPeriod, created.Id));
        Assert.Equal(ChangeLogAction.Created, afterCreate.Action);
        Assert.Equal(ChangeLogEntityType.AcademicPeriod, afterCreate.EntityType);
        Assert.Equal(ApiFactory.ManagerEmail, afterCreate.ActorEmail);

        await manager.PutJsonAsync<PeriodDto>($"/api/manager/periods/{created.Id}",
            new { name = created.Name, startDate = "2031-01-01", endDate = "2031-06-30" });
        Assert.Single(await LogsFor(manager, ChangeLogEntityType.AcademicPeriod, created.Id));

        await manager.PutJsonAsync<PeriodDto>($"/api/manager/periods/{created.Id}",
            new { name = Unique("CL-P2"), startDate = "2031-02-01", endDate = "2031-07-01" });
        Assert.Equal(2, (await LogsFor(manager, ChangeLogEntityType.AcademicPeriod, created.Id)).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/periods/{created.Id}")).StatusCode);
        var afterDelete = await LogsFor(manager, ChangeLogEntityType.AcademicPeriod, created.Id);
        Assert.Equal(ChangeLogAction.Deleted, afterDelete[^1].Action);
        Assert.False(afterDelete[^1].EntityExists);
    }

    [Fact]
    public async Task Student_cannot_read_reference_change_log_types()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.GetAsync("/api/manager/change-log?entityType=Group&page=1&pageSize=10")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.GetAsync("/api/manager/change-log?entityType=Discipline&page=1&pageSize=10")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.GetAsync("/api/manager/change-log?entityType=AcademicPeriod&page=1&pageSize=10")).StatusCode);
    }

    [Fact]
    public async Task Discipline_and_period_in_use_delete_do_not_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        await manager.PostJsonAsync<GradeDto>("/api/manager/grades", new
        {
            studentId = student.Id,
            disciplineId = discipline.Id,
            periodId = period.Id,
            value = 80
        }, HttpStatusCode.Created);

        var discLogs = await CountFor(manager, ChangeLogEntityType.Discipline, discipline.Id);
        var periodLogs = await CountFor(manager, ChangeLogEntityType.AcademicPeriod, period.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/manager/disciplines/{discipline.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/manager/periods/{period.Id}")).StatusCode);
        Assert.Equal(discLogs, await CountFor(manager, ChangeLogEntityType.Discipline, discipline.Id));
        Assert.Equal(periodLogs, await CountFor(manager, ChangeLogEntityType.AcademicPeriod, period.Id));
    }

    [Fact]
    public async Task Failed_SaveChanges_on_group_update_rolls_back_group_and_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var originalName = group.Name;
        var logCountBefore = await CountForDb(group.Id);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync($"/api/manager/groups/{group.Id}", new
            {
                name = Unique("ShouldNotPersist"),
                startDate = "2030-03-01",
                endDate = "2030-09-01"
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
        var persisted = await db.Groups.AsNoTracking().SingleAsync(g => g.Id == group.Id);
        Assert.Equal(originalName, persisted.Name);
        Assert.Equal(logCountBefore, await db.ChangeLogEntries.CountAsync(e => e.EntityId == group.Id));
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == group.Id && e.Action == ChangeLogAction.Updated));
    }

    [Fact]
    public async Task Failed_SaveChanges_on_period_create_rolls_back()
    {
        var manager = await ManagerAsync();
        var logsBefore = await TotalLogsDb();
        var name = Unique("AtomicPeriod");

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/periods",
                new { name, startDate = "2032-01-01", endDate = "2032-06-01" });
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
        Assert.False(await db.Periods.AnyAsync(p => p.Name == name));
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
    }

    [Fact]
    public async Task Failed_SaveChanges_on_discipline_update_rolls_back()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager, "keep-me");
        var originalName = discipline.Name;
        var logCountBefore = await CountForDb(discipline.Id);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync($"/api/manager/disciplines/{discipline.Id}",
                new { name = Unique("ShouldNotPersist"), description = "poison-text" });
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
        var persisted = await db.Disciplines.AsNoTracking().SingleAsync(d => d.Id == discipline.Id);
        Assert.Equal(originalName, persisted.Name);
        Assert.Equal("keep-me", persisted.Description);
        Assert.Equal(logCountBefore, await db.ChangeLogEntries.CountAsync(e => e.EntityId == discipline.Id));
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == discipline.Id && e.Action == ChangeLogAction.Updated));
    }

    private static async Task<List<ChangeLogListItemDto>> LogsFor(ApiClient manager, ChangeLogEntityType type, Guid entityId)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            $"/api/manager/change-log?entityType={type}&page=1&pageSize=100");
        return page.Items.Where(i => i.EntityId == entityId).OrderBy(i => i.OccurredAt).ThenBy(i => i.Id).ToList();
    }

    private static async Task<int> CountFor(ApiClient manager, ChangeLogEntityType type, Guid entityId) =>
        (await LogsFor(manager, type, entityId)).Count;

    private static async Task<int> TotalCount(ApiClient manager)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>("/api/manager/change-log?page=1&pageSize=1");
        return page.Total;
    }

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
}
