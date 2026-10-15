using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MbaLms.Api.Tests;

public class StudentChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Student_crud_markers_group_password_reset_no_op_and_delete()
    {
        var manager = await ManagerAsync();
        var groupA = await CreateGroupAsync(manager);
        var groupB = await CreateGroupAsync(manager);
        var secret = $"{Unique("stu")}@test.local";

        var created = await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = Unique("CL-S"),
            firstName = "Иван",
            middleName = "Иванович",
            email = secret,
            groupId = groupA.Id,
            password = "Student-Pass-1"
        }, HttpStatusCode.Created);

        var createLog = Assert.Single(await LogsFor(manager, ChangeLogEntityType.Student, created.Id));
        Assert.Equal(ChangeLogAction.Created, createLog.Action);
        Assert.Equal(ApiFactory.ManagerEmail, createLog.ActorEmail);
        Assert.Equal(created.FullName, createLog.EntityTitle);
        Assert.Contains(createLog.Changes, c => c.Field == "email" && c.New?.GetString() == "set");
        Assert.Contains(createLog.Changes, c => c.Field == "password" && c.New?.GetString() == "set");
        Assert.Contains(createLog.Changes, c => c.Field == "group" && c.New?.GetString() == groupA.Name);
        var userAfterCreate = await FindUserByEmailAsync(secret);
        Assert.NotNull(userAfterCreate);
        AssertNoStudentSecrets(createLog, "Student-Pass-1", secret);
        AssertNoIdentityMaterialInCl(createLog, userAfterCreate);

        await manager.PutJsonAsync<StudentDto>($"/api/manager/students/{created.Id}", new
        {
            lastName = created.LastName,
            firstName = created.FirstName,
            middleName = created.MiddleName,
            email = secret,
            groupId = groupA.Id,
            password = (string?)null
        });
        Assert.Single(await LogsFor(manager, ChangeLogEntityType.Student, created.Id));

        await manager.PutJsonAsync<StudentDto>($"/api/manager/students/{created.Id}", new
        {
            lastName = created.LastName,
            firstName = created.FirstName,
            middleName = created.MiddleName,
            email = secret,
            groupId = groupA.Id,
            password = "Student-Pass-2"
        });
        var afterPwd = await LogsFor(manager, ChangeLogEntityType.Student, created.Id);
        Assert.Equal(2, afterPwd.Count);
        Assert.Equal(ChangeLogAction.Updated, afterPwd[1].Action);
        Assert.Single(afterPwd[1].Changes);
        Assert.Contains(afterPwd[1].Changes, c => c.Field == "password" && c.New?.GetString() == "reset");
        var userAfterPwd = await FindUserByEmailAsync(secret);
        Assert.NotNull(userAfterPwd);
        AssertNoStudentSecrets(afterPwd[1], "Student-Pass-2", secret);
        AssertNoIdentityMaterialInCl(afterPwd[1], userAfterPwd);

        var newEmail = $"{Unique("chg")}@test.local";
        await manager.PutJsonAsync<StudentDto>($"/api/manager/students/{created.Id}", new
        {
            lastName = Unique("Renamed"),
            firstName = "Пётр",
            middleName = (string?)null,
            email = newEmail,
            groupId = groupB.Id,
            password = (string?)null
        });
        var afterProfile = await LogsFor(manager, ChangeLogEntityType.Student, created.Id);
        Assert.Equal(3, afterProfile.Count);
        Assert.Contains(afterProfile[2].Changes, c => c.Field == "email" && c.New?.GetString() == "changed");
        Assert.Contains(afterProfile[2].Changes, c => c.Field == "group");
        Assert.Contains(afterProfile[2].Changes, c => c.Field == "lastName");
        var userAfterProfile = await FindUserByEmailAsync(newEmail);
        Assert.NotNull(userAfterProfile);
        AssertNoStudentSecrets(afterProfile[2], secret, newEmail);
        AssertNoIdentityMaterialInCl(afterProfile[2], userAfterProfile);

        var titleBeforeDelete = afterProfile[^1].EntityTitle;
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/students/{created.Id}")).StatusCode);
        var deleted = await LogsFor(manager, ChangeLogEntityType.Student, created.Id);
        Assert.Equal(4, deleted.Count);
        Assert.Equal(ChangeLogAction.Deleted, deleted[^1].Action);
        Assert.False(deleted[^1].EntityExists);
        Assert.Equal(titleBeforeDelete, deleted[^1].EntityTitle);
        Assert.Contains(deleted[^1].Changes, c => c.Field == "group");
        Assert.DoesNotContain(deleted[^1].Changes, c => c.Field == "email");
        Assert.DoesNotContain(deleted[^1].Changes, c => c.Field == "password");
        AssertNoStudentSecrets(deleted[^1], secret, newEmail);
        AssertNoIdentityMaterialInCl(deleted[^1], userAfterProfile);
    }

    [Fact]
    public async Task Student_in_use_delete_does_not_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var logsBefore = await CountFor(manager, ChangeLogEntityType.Student, account.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        await manager.PostJsonAsync<GradeDto>("/api/manager/grades", new
        {
            studentId = account.Id,
            disciplineId = discipline.Id,
            periodId = period.Id,
            value = 80
        }, HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/manager/students/{account.Id}")).StatusCode);
        Assert.Equal(logsBefore, await CountFor(manager, ChangeLogEntityType.Student, account.Id));
    }

    [Fact]
    public async Task Student_cannot_read_student_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.GetAsync("/api/manager/change-log?entityType=Student&page=1&pageSize=10")).StatusCode);
    }

    [Fact]
    public async Task Failed_SaveChanges_after_identity_create_rolls_back_user_student_and_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("atomic")}@test.local";
        var logsBefore = await TotalLogsDb();
        var usersBefore = await CountUsersDb();
        var roleLinksBefore = await CountUserRolesDb();

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/students", new
            {
                lastName = Unique("AtomicS"),
                firstName = "Test",
                email,
                groupId = group.Id,
                password = "Student-Pass-1"
            });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawSuccessfulIntermediateSaveChanges);
        Assert.True(Factory.SaveFail.SawIdentityUserInsert);
        Assert.True(Factory.SaveFail.SawIdentityRoleAssign);
        Assert.Equal(email, Factory.SaveFail.CapturedMidTxUserEmail);
        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.False(await db.Students.AnyAsync(s => s.Email == email));
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
        Assert.Equal(usersBefore, await CountUsersDb());
        Assert.Equal(roleLinksBefore, await CountUserRolesDb());
        Assert.Null(await FindUserByEmailAsync(email));
        Assert.Equal(0, await CountRoleLinksForEmailAsync(email));
    }

    [Fact]
    public async Task Failed_SaveChanges_after_identity_update_rolls_back_profile_and_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var originalEmail = account.Email;
        var logCountBefore = await CountForDb(account.Id);
        var newEmail = $"{Unique("nope")}@test.local";

        var beforeUser = await FindUserByEmailAsync(originalEmail);
        Assert.NotNull(beforeUser);
        var beforeUserName = beforeUser.UserName;
        var beforeNormalizedEmail = beforeUser.NormalizedEmail;
        var beforeNormalizedUserName = beforeUser.NormalizedUserName;
        var beforePasswordHash = beforeUser.PasswordHash;
        var beforeSecurityStamp = beforeUser.SecurityStamp;
        var beforeRoles = await GetRolesAsync(beforeUser.Id);
        Assert.Contains(Roles.Student, beforeRoles);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync($"/api/manager/students/{account.Id}", new
            {
                lastName = Unique("ShouldNotPersist"),
                firstName = "X",
                email = newEmail,
                groupId = group.Id,
                password = "Student-Pass-9"
            });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawSuccessfulIntermediateSaveChanges);
        Assert.Equal(newEmail, Factory.SaveFail.CapturedMidTxUserEmail);
        Assert.True(Factory.SaveFail.CapturedMidTxPasswordHashModified);
        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        var persisted = await db.Students.AsNoTracking().SingleAsync(s => s.Id == account.Id);
        Assert.Equal(originalEmail, persisted.Email);
        Assert.Equal("Иван", persisted.FirstName);
        Assert.Equal(logCountBefore, await db.ChangeLogEntries.CountAsync(e => e.EntityId == account.Id));
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == account.Id && e.Action == ChangeLogAction.Updated));

        var user = await FindUserByEmailAsync(originalEmail);
        Assert.NotNull(user);
        Assert.Null(await FindUserByEmailAsync(newEmail));
        Assert.Equal(beforeUserName, user.UserName);
        Assert.Equal(beforeNormalizedEmail, user.NormalizedEmail);
        Assert.Equal(beforeNormalizedUserName, user.NormalizedUserName);
        Assert.Equal(beforePasswordHash, user.PasswordHash);
        Assert.Equal(beforeSecurityStamp, user.SecurityStamp);
        Assert.Equal(beforeRoles.OrderBy(r => r), (await GetRolesAsync(user.Id)).OrderBy(r => r));
    }

    [Fact]
    public async Task Failed_identity_delete_after_student_change_log_saved_rolls_back_all()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var logsBefore = await CountForDb(account.Id);
        var userId = (await NewDb().Students.AsNoTracking().SingleAsync(s => s.Id == account.Id)).UserId;
        var rolesBefore = await GetRolesAsync(userId);
        Assert.Contains(Roles.Student, rolesBefore);
        var roleLinksBefore = await CountUserRolesDb();

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.DeleteAsync($"/api/manager/students/{account.Id}");
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawSuccessfulIntermediateSaveChanges);
        Assert.True(Factory.SaveFail.SawIdentityUserDeleteOnFailingSave);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.True(await db.Students.AnyAsync(s => s.Id == account.Id));
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync(e => e.EntityId == account.Id));
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == account.Id && e.Action == ChangeLogAction.Deleted));
        var user = await FindUserByIdAsync(userId);
        Assert.NotNull(user);
        Assert.Equal(rolesBefore.OrderBy(r => r), (await GetRolesAsync(userId)).OrderBy(r => r));
        Assert.Equal(roleLinksBefore, await CountUserRolesDb());
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

    private async Task<int> CountUsersDb()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.Users.CountAsync();
    }

    private async Task<AppUser?> FindUserByEmailAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.FindByEmailAsync(email);
    }

    private async Task<AppUser?> FindUserByIdAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.FindByIdAsync(id.ToString());
    }

    private async Task<List<string>> GetRolesAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("user missing");
        return (await users.GetRolesAsync(user)).ToList();
    }

    private async Task<int> CountUserRolesDb()
    {
        await using var db = NewDb();
        return await db.Set<IdentityUserRole<Guid>>().CountAsync();
    }

    private async Task<int> CountRoleLinksForEmailAsync(string email)
    {
        await using var db = NewDb();
        var normalized = email.ToUpperInvariant();
        return await (
            from ur in db.Set<IdentityUserRole<Guid>>()
            join u in db.Users on ur.UserId equals u.Id
            where u.NormalizedEmail == normalized
            select ur).CountAsync();
    }

    private static void AssertNoStudentSecrets(ChangeLogListItemDto entry, params string[] secrets)
    {
        var blob = JsonSerializer.Serialize(entry);
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, blob);
    }

    /// <summary>CL must not contain Identity secret field names or the user's hash/stamp values.</summary>
    private static void AssertNoIdentityMaterialInCl(ChangeLogListItemDto entry, AppUser user)
    {
        var blob = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("passwordHash", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("concurrencyStamp", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authenticatorKey", blob, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(user.PasswordHash))
            Assert.DoesNotContain(user.PasswordHash, blob);
        if (!string.IsNullOrEmpty(user.SecurityStamp))
            Assert.DoesNotContain(user.SecurityStamp, blob);
        if (!string.IsNullOrEmpty(user.ConcurrencyStamp))
            Assert.DoesNotContain(user.ConcurrencyStamp, blob);
    }
}
