using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Email;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class AdminAccessTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Owner_without_academic_roles_cannot_use_manager_or_student_api()
    {
        var (owner, _, email, _) = await CreateOwnerUserAsync();
        var me = await owner.GetJsonAsync<MeDto>("/api/auth/me");
        Assert.Equal(Roles.Owner, me.Role);
        Assert.Equal([Roles.Owner], me.Roles);
        Assert.Equal(email, me.Email);

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/manager/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/student/dashboard")).StatusCode);
    }

    [Fact]
    public async Task AccountAdmin_cannot_use_manager_or_student_api()
    {
        var (admin, _, _, _) = await CreateAccountAdminAsync();
        var me = await admin.GetJsonAsync<MeDto>("/api/auth/me");
        Assert.Equal(Roles.AccountAdmin, me.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/manager/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/student/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Invite_manager_uses_login_and_setup_link_then_set_password_allows_login()
    {
        var (owner, _, _, _) = await CreateOwnerUserAsync();
        var email = $"{Unique("newmgr")}@test.local";
        var invited = await owner.PostJsonAsync<StaffInviteResultDto>("/api/admin/staff/managers",
            new { email, role = Roles.Owner, password = "Should-Not-Stick-1" }, HttpStatusCode.OK);
        Assert.True(invited.Queued);
        Assert.NotNull(invited.DevelopmentPreviewId);

        var preview = await owner.GetJsonAsync<CapturedEmailDto>($"/api/dev/mail/{invited.DevelopmentPreviewId}");
        Assert.Equal(email, preview.To);
        Assert.Equal(PasswordEmailComposer.PurposeInvite, preview.Purpose);
        Assert.Contains(email, preview.TextBody);
        Assert.Contains("/set-password?", preview.TextBody);
        Assert.DoesNotContain("Should-Not-Stick-1", preview.TextBody);

        var list = await owner.GetJsonAsync<PagedResult<StaffDto>>("/api/admin/staff?page=1&pageSize=200");
        var row = Assert.Single(list.Items, s => s.Email == email);
        Assert.Equal([Roles.Manager], row.Roles);

        var (linkEmail, token) = ParseSetupLink(preview.TextBody);
        var anon = await AnonymousAsync();
        const string newPassword = "Fresh-Manager-9";
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.PostAsync("/api/auth/set-password", new { email = linkEmail, token, newPassword })).StatusCode);

        var signedIn = NewClient();
        await signedIn.LoginAsync(email, newPassword);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/manager/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.GetAsync("/api/admin/staff")).StatusCode);
    }

    [Fact]
    public async Task AccountAdmin_cannot_act_on_owner_or_account_admin_even_with_id_substitution()
    {
        var (owner, ownerId, _, _) = await CreateOwnerUserAsync();
        var (admin, adminId, _, _) = await CreateAccountAdminAsync();
        var extra = await CreateAccountAdminAsync();

        var listed = await admin.GetJsonAsync<PagedResult<StaffDto>>("/api/admin/staff?page=1&pageSize=200");
        Assert.DoesNotContain(listed.Items, s => s.Id == ownerId || s.Id == adminId || s.Id == extra.Id);

        var audit = await admin.GetJsonAsync<PagedResult<AdminAuditDto>>("/api/admin/audit?page=1&pageSize=200");
        Assert.All(audit.Items, e => Assert.Equal(Roles.Manager, e.TargetKind));

        Assert.Equal("protected_account", await ErrorCode(admin.PostAsync($"/api/admin/staff/{ownerId}/block", new { role = Roles.Manager })));
        Assert.Equal("protected_account", await ErrorCode(admin.PostAsync($"/api/admin/staff/{extra.Id}/block")));
        Assert.Equal("protected_account", await ErrorCode(admin.PostAsync($"/api/admin/staff/{ownerId}/unblock")));
        Assert.Equal("protected_account", await ErrorCode(admin.PostAsync($"/api/admin/staff/{ownerId}/revoke-owner")));
        Assert.Equal("protected_account", await ErrorCode(admin.DeleteAsync($"/api/admin/staff/{ownerId}")));

        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var ownerUser = await users.FindByIdAsync(ownerId.ToString());
        Assert.NotNull(ownerUser);
        Assert.Contains(Roles.Owner, await users.GetRolesAsync(ownerUser));
        Assert.False(ownerUser.LockoutEnd > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Blocking_manager_revokes_session_and_keeps_academic_authorship()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades", new
        {
            studentId = student.Id,
            disciplineId = (await CreateDisciplineAsync(manager)).Id,
            periodId = (await CreatePeriodAsync(manager)).Id,
            value = 77
        }, HttpStatusCode.Created);

        var (other, otherEmail, otherPassword) = await CreateManagerAsync();
        Guid otherId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            otherId = (await users.FindByEmailAsync(otherEmail))!.Id;
        }

        var (owner, _, _, _) = await CreateOwnerUserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/admin/staff/{otherId}/block")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/manager/groups")).StatusCode);
        var locked = await NewClient().TryLoginAsync(otherEmail, otherPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("locked_out", await locked.ErrorCodeAsync());

        var still = await manager.GetJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}");
        Assert.Equal(77, still.Value);

        var journal = await owner.GetJsonAsync<PagedResult<AdminAuditDto>>("/api/admin/audit?page=1&pageSize=50");
        var entry = Assert.Single(journal.Items, e => e.Action == AdminAuditAction.ManagerBlocked && e.TargetEmail == otherEmail);
        var json = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("token=", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherPassword, json);
        Assert.DoesNotContain("/set-password", json);
    }

    [Fact]
    public async Task Last_owner_cannot_be_blocked_revoked_or_deleted()
    {
        var (owner, ownerId, _, _) = await CreateOwnerUserAsync();
        await RemoveOwnerRoleExceptAsync(ownerId);
        Assert.Equal("last_owner", await ErrorCode(owner.PostAsync($"/api/admin/staff/{ownerId}/block")));
        Assert.Equal("last_owner", await ErrorCode(owner.PostAsync($"/api/admin/staff/{ownerId}/revoke-owner")));
        Assert.Equal("last_owner", await ErrorCode(owner.DeleteAsync($"/api/admin/staff/{ownerId}")));

        var second = await CreateOwnerUserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/admin/staff/{second.Id}/revoke-owner")).StatusCode);
        Assert.Equal("last_owner", await ErrorCode(owner.PostAsync($"/api/admin/staff/{ownerId}/block")));
    }

    [Fact]
    public async Task Dev_staff_fixture_creates_synthetic_owner_in_testing_and_rejects_manager_role()
    {
        var manager = await ManagerAsync();
        var email = $"{Unique("fxowner")}@test.local";
        var created = await manager.PostJsonAsync<StaffFixtureDto>("/api/dev/staff-fixture",
            new { email, password = "Owner-E2e-1", role = Roles.Owner }, HttpStatusCode.Created);
        Assert.Equal(Roles.Owner, created.Role);

        var again = await manager.PostJsonAsync<StaffFixtureDto>("/api/dev/staff-fixture",
            new { email, password = "Other-Pass-9", role = Roles.Owner });
        Assert.Equal(created.Id, again.Id);

        var owner = NewClient();
        await owner.LoginAsync(email, "Owner-E2e-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/manager/groups")).StatusCode);

        var badRole = await manager.PostAsync("/api/dev/staff-fixture",
            new { email = $"{Unique("fxmgr")}@test.local", password = "Manager-E2e-1", role = Roles.Manager });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

        var student = await StudentAsync(await CreateStudentAsync(manager, (await CreateGroupAsync(manager)).Id));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.PostAsync("/api/dev/staff-fixture",
                new { email = $"{Unique("fxstu")}@test.local", password = "Student-E2e-1", role = Roles.Owner })).StatusCode);
    }

    [Fact]
    public async Task Dev_staff_fixture_is_off_in_development_without_the_explicit_flag()
    {
        await using var custom = Factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting(StaffFixtureAvailability.ConfigKey, "false");
        });
        var email = $"{Unique("devoff")}@test.local";
        var before = await SnapshotIdentitiesAsync();
        var api = ClientOn(custom);
        await api.LoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/dev/staff-fixture",
            new { email, password = "Owner-E2e-1", role = Roles.Owner })).StatusCode);
        Assert.Equal(before, await SnapshotIdentitiesAsync());
        Assert.Null(await FindUserEmailAsync(email));
    }

    [Fact]
    public async Task Dev_staff_fixture_is_unregistered_in_production_even_with_flag()
    {
        await using var custom = Factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting(StaffFixtureAvailability.ConfigKey, "true");
        });
        Assert.DoesNotContain(MappedRoutes(custom), r => r.Contains("staff-fixture", StringComparison.OrdinalIgnoreCase));

        var managerEmail = $"{Unique("prodfx")}@test.local";
        var ownerAttempt = $"{Unique("prodown")}@test.local";
        var anonAttempt = $"{Unique("prodanon")}@test.local";
        var (_, _, ownerEmail, ownerPassword) = await CreateOwnerUserAsync();
        var before = await SnapshotIdentitiesAsync();

        var bodyFor = (string email) => new { email, password = "Owner-E2e-1", role = Roles.Owner };

        var anonymous = ClientOn(custom);
        await anonymous.RefreshCsrfAsync();
        var anonStatus = (await anonymous.PostAsync("/api/dev/staff-fixture", bodyFor(anonAttempt))).StatusCode;
        Assert.False((int)anonStatus is >= 200 and < 300, $"anon fixture {(int)anonStatus}");

        var manager = ClientOn(custom);
        await manager.LoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync("/api/dev/staff-fixture", bodyFor(managerEmail))).StatusCode);

        var owner = ClientOn(custom);
        await owner.LoginAsync(ownerEmail, ownerPassword);
        var ownerStatus = (await owner.PostAsync("/api/dev/staff-fixture", bodyFor(ownerAttempt))).StatusCode;
        Assert.False((int)ownerStatus is >= 200 and < 300, $"owner fixture {(int)ownerStatus}");

        Assert.Equal(before, await SnapshotIdentitiesAsync());
        Assert.Null(await FindUserEmailAsync(managerEmail));
        Assert.Null(await FindUserEmailAsync(ownerAttempt));
        Assert.Null(await FindUserEmailAsync(anonAttempt));
    }

    [Fact]
    public async Task Parallel_block_of_two_owners_keeps_an_effective_owner()
    {
        var first = await CreateOwnerUserAsync();
        var second = await CreateOwnerUserAsync();
        await RemoveOwnerRoleExceptAsync(first.Id, second.Id);

        var a = first.Client.PostAsync($"/api/admin/staff/{second.Id}/block");
        var b = second.Client.PostAsync($"/api/admin/staff/{first.Id}/block");
        await Task.WhenAll(a, b);

        Assert.True(await CountEffectiveOwnersAsync() >= 1, "effective owners after parallel block");
        var codes = new[] { (await a).StatusCode, (await b).StatusCode };
        Assert.Contains(HttpStatusCode.NoContent, codes);
        Assert.Contains(HttpStatusCode.Conflict, codes);
    }

    [Fact]
    public async Task Parallel_revoke_of_two_owners_keeps_an_effective_owner()
    {
        var first = await CreateOwnerUserAsync();
        var second = await CreateOwnerUserAsync();
        await RemoveOwnerRoleExceptAsync(first.Id, second.Id);

        var a = first.Client.PostAsync($"/api/admin/staff/{second.Id}/revoke-owner");
        var b = second.Client.PostAsync($"/api/admin/staff/{first.Id}/revoke-owner");
        await Task.WhenAll(a, b);

        Assert.True(await CountEffectiveOwnersAsync() >= 1, "effective owners after parallel revoke");
        Assert.True(await UserHasOwnerRoleAsync(first.Id) || await UserHasOwnerRoleAsync(second.Id));
        var codes = new[] { (await a).StatusCode, (await b).StatusCode };
        Assert.True(codes.Count(c => c == HttpStatusCode.NoContent) <= 1, $"both revokes succeeded: {string.Join(',', codes)}");
    }

    [Fact]
    public async Task Parallel_delete_of_two_owners_keeps_an_effective_owner()
    {
        var first = await CreateOwnerUserAsync();
        var second = await CreateOwnerUserAsync();
        await RemoveOwnerRoleExceptAsync(first.Id, second.Id);

        var a = first.Client.DeleteAsync($"/api/admin/staff/{second.Id}");
        var b = second.Client.DeleteAsync($"/api/admin/staff/{first.Id}");
        await Task.WhenAll(a, b);

        Assert.True(await CountEffectiveOwnersAsync() >= 1, "effective owners after parallel delete");
        Assert.True(await UserExistsAsync(first.Id) && await UserExistsAsync(second.Id));
        Assert.True(await UserHasOwnerRoleAsync(first.Id) && await UserHasOwnerRoleAsync(second.Id));
        foreach (var res in new[] { await a, await b })
            Assert.False((int)res.StatusCode is >= 200 and < 300, $"owner delete {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Parallel_mixed_block_revoke_delete_keeps_an_effective_owner()
    {
        var first = await CreateOwnerUserAsync();
        var second = await CreateOwnerUserAsync();
        await RemoveOwnerRoleExceptAsync(first.Id, second.Id);

        var block = first.Client.PostAsync($"/api/admin/staff/{second.Id}/block");
        var revoke = second.Client.PostAsync($"/api/admin/staff/{first.Id}/revoke-owner");
        var delete = first.Client.DeleteAsync($"/api/admin/staff/{second.Id}");
        await Task.WhenAll(block, revoke, delete);

        Assert.True(await CountEffectiveOwnersAsync() >= 1, "effective owners after mixed parallel mutations");
        Assert.True(await UserHasOwnerRoleAsync(first.Id) || await UserHasOwnerRoleAsync(second.Id));
    }

    private static async Task<string> ErrorCode(Task<HttpResponseMessage> pending)
    {
        var res = await pending;
        var code = await res.ErrorCodeAsync();
        Assert.False(string.IsNullOrEmpty(code), $"missing error code {(int)res.StatusCode}");
        return code!;
    }

    private static ApiClient ClientOn(WebApplicationFactory<Program> host) =>
        new(host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false }));

    private static IReadOnlyList<string> MappedRoutes(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();

    private async Task<(int Users, int Links, string Identity)> SnapshotIdentitiesAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => u.Id + ":" + u.Email + ":" + u.SecurityStamp + ":" + u.LockoutEnd)
            .ToListAsync();
        var links = await db.UserRoles.AsNoTracking()
            .OrderBy(ur => ur.UserId).ThenBy(ur => ur.RoleId)
            .Select(ur => ur.UserId + ":" + ur.RoleId)
            .ToListAsync();
        return (users.Count, links.Count, string.Join("|", users.Concat(links)));
    }

    private async Task<string?> FindUserEmailAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return (await users.FindByEmailAsync(email))?.Email;
    }

    private async Task<bool> UserExistsAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == id);
    }

    private async Task<bool> UserHasOwnerRoleAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(id.ToString());
        return user is not null && await users.IsInRoleAsync(user, Roles.Owner);
    }

    private static (string Email, string Token) ParseSetupLink(string body)
    {
        var m = Regex.Match(body, @"https?://\S*/set-password\?email=([^&\s]+)&token=([^\s]+)");
        Assert.True(m.Success, "setup link not found in email body");
        var token = m.Groups[2].Value.TrimEnd('.', ')', ']');
        return (Uri.UnescapeDataString(m.Groups[1].Value), Uri.UnescapeDataString(token));
    }
}
