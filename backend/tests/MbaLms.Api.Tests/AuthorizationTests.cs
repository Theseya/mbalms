using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

/// <summary>
/// Authorization is enforced by the server for every registered endpoint. The endpoint list is read from routing,
/// so a newly added endpoint is covered automatically.
/// </summary>
public partial class AuthorizationTests(ApiFactory factory) : TestBase(factory)
{
    private static readonly string[] PublicEndpoints = ["GET api/auth/csrf", "POST api/auth/login", "GET health"];

    private record Endpoint(string Method, string Pattern, bool AllowsAnonymous)
    {
        public string Url => "/" + RouteParameter().Replace(Pattern, _ => Guid.NewGuid().ToString());
        public bool HasParameters => Pattern.Contains('{');
        public override string ToString() => $"{Method} {Pattern}";
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();

    private List<Endpoint> Endpoints() =>
        Factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => new Endpoint(m, e.RoutePattern.RawText!.TrimStart('/'), e.Metadata.GetMetadata<IAllowAnonymous>() is not null)))
            .ToList();

    private static Task<HttpResponseMessage> SendAsync(ApiClient client, Endpoint endpoint)
    {
        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), endpoint.Url);
        if (endpoint.Method is "POST" or "PUT")
        {
            if (endpoint.Pattern.Contains("imports/", StringComparison.Ordinal) && endpoint.Pattern.EndsWith("/preview", StringComparison.Ordinal))
            {
                var file = new ByteArrayContent([]);
                file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
                var form = new MultipartFormDataContent();
                form.Add(file, "file", "probe.xlsx");
                request.Content = form;
            }
            else
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        return client.Http.SendAsync(request);
    }

    [Fact]
    public void Only_login_csrf_and_health_are_public()
    {
        var anonymous = Endpoints().Where(e => e.AllowsAnonymous).Select(e => e.ToString()).Order();
        Assert.Equal(PublicEndpoints.Order(), anonymous);
    }

    [Fact]
    public async Task Every_protected_endpoint_rejects_anonymous_requests()
    {
        var client = await AnonymousAsync();
        var protectedEndpoints = Endpoints().Where(e => !e.AllowsAnonymous).ToList();
        Assert.NotEmpty(protectedEndpoints);

        var failures = new List<string>();
        foreach (var endpoint in protectedEndpoints)
        {
            var res = await SendAsync(client, endpoint);
            if (res.StatusCode != HttpStatusCode.Unauthorized) failures.Add($"{endpoint} -> {(int)res.StatusCode}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Student_is_forbidden_on_every_manager_endpoint()
    {
        var manager = await ManagerAsync();
        var student = await StudentAsync(await CreateStudentAsync(manager, (await CreateGroupAsync(manager)).Id));

        var failures = new List<string>();
        foreach (var endpoint in Endpoints().Where(e => e.Pattern.StartsWith("api/manager/")))
        {
            var res = await SendAsync(student, endpoint);
            if (res.StatusCode != HttpStatusCode.Forbidden) failures.Add($"{endpoint} -> {(int)res.StatusCode}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Manager_is_forbidden_on_every_student_endpoint()
    {
        var manager = await ManagerAsync();
        var failures = new List<string>();
        foreach (var endpoint in Endpoints().Where(e => e.Pattern.StartsWith("api/student/")))
        {
            var res = await SendAsync(manager, endpoint);
            if (res.StatusCode != HttpStatusCode.Forbidden) failures.Add($"{endpoint} -> {(int)res.StatusCode}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Manager_can_read_every_manager_list_and_export()
    {
        var manager = await ManagerAsync();
        var failures = new List<string>();
        foreach (var endpoint in Endpoints().Where(e => e.Method == "GET" && e.Pattern.StartsWith("api/manager/") && !e.HasParameters))
        {
            var res = await SendAsync(manager, endpoint);
            if (res.StatusCode != HttpStatusCode.OK) failures.Add($"{endpoint} -> {(int)res.StatusCode}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Student_cannot_read_or_change_another_students_data()
    {
        var manager = await ManagerAsync();
        var groupA = await CreateGroupAsync(manager);
        var groupB = await CreateGroupAsync(manager);
        var accountA = await CreateStudentAsync(manager, groupA.Id, "Алексеева");
        var accountB = await CreateStudentAsync(manager, groupB.Id, "Борисов");
        var studentA = await StudentAsync(accountA);
        var studentB = await StudentAsync(accountB);

        var lesson = await CreateLessonAsync(manager, groupA.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = accountA.Id, disciplineId = discipline.Id, periodId = period.Id, value = 88 }, HttpStatusCode.Created);
        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");
        var survey = await manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys", new
        {
            type = "ServiceSurvey", title = Unique("Опрос"), groupId = groupA.Id,
            questions = new object[] { new { text = "Комментарий", type = "Text", isRequired = true } }
        }, HttpStatusCode.Created);
        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");
        var answer = new { answers = new object[] { new { questionId = survey.Questions[0].Id, textValue = "Личный ответ A" } } };
        await (await studentA.PostAsync($"/api/student/surveys/{survey.Id}/responses", answer)).EnsureStatusAsync(HttpStatusCode.Created);

        var notificationOfA = (await studentA.GetJsonAsync<List<NotificationDto>>("/api/notifications")).First();

        // Student B passes A's identifiers explicitly: the server ignores or rejects them.
        Assert.Empty(await studentB.GetJsonAsync<List<StudentGradeDto>>($"/api/student/grades?studentId={accountA.Id}"));
        Assert.DoesNotContain(await studentB.GetJsonAsync<List<StudentLessonDto>>($"/api/student/schedule?groupId={groupA.Id}"),
            l => l.Id == lesson.Id);
        Assert.Empty(await studentB.GetJsonAsync<List<StudentSurveyListItemDto>>("/api/student/surveys"));
        Assert.Equal(HttpStatusCode.NotFound, (await studentB.GetAsync($"/api/student/surveys/{survey.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await studentB.PostAsync($"/api/student/surveys/{survey.Id}/responses", answer)).StatusCode);
        Assert.Empty(await studentB.GetJsonAsync<List<NotificationDto>>($"/api/notifications?userId={accountA.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await studentB.PostAsync($"/api/notifications/{notificationOfA.Id}/read")).StatusCode);
        (await studentB.PostAsync("/api/notifications/read-all")).EnsureSuccessStatusCode();

        Assert.Null((await studentA.GetJsonAsync<List<NotificationDto>>("/api/notifications")).Single(n => n.Id == notificationOfA.Id).ReadAt);
        Assert.Contains(await studentA.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"), g => g.Id == grade.Id);
    }

    [Fact]
    public async Task Password_reset_by_manager_ends_existing_student_sessions()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var student = await StudentAsync(account);
        (await student.GetAsync("/api/student/dashboard")).EnsureSuccessStatusCode();

        var dto = await manager.GetJsonAsync<StudentDto>($"/api/manager/students/{account.Id}");
        await manager.PutJsonAsync<StudentDto>($"/api/manager/students/{account.Id}", new
        {
            lastName = dto.LastName, firstName = dto.FirstName, email = dto.Email, groupId = dto.GroupId, password = "New-Pass-2026"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await student.GetAsync("/api/student/dashboard")).StatusCode);
        var relogin = NewClient();
        await relogin.LoginAsync(account.Email, "New-Pass-2026");
    }

    [Fact]
    public async Task Deleted_student_loses_access()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var student = await StudentAsync(account);

        await (await manager.DeleteAsync($"/api/manager/students/{account.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await student.GetAsync("/api/student/grades")).StatusCode);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_get_the_same_answer()
    {
        var unknown = await NewClient().TryLoginAsync($"{Unique("nobody")}@test.local", "Some-Pass-1");
        var wrong = await NewClient().TryLoginAsync(ApiFactory.ManagerEmail, "Some-Pass-1");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(await wrong.ErrorCodeAsync(), await unknown.ErrorCodeAsync());
    }

    [Fact]
    public async Task Login_without_csrf_token_is_rejected()
    {
        var client = NewClient();
        var res = await client.Http.PostAsync("/api/auth/login",
            new StringContent($$"""{"email":"{{ApiFactory.ManagerEmail}}","password":"{{ApiFactory.ManagerPassword}}"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("csrf_failed", await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Session_cookie_is_http_only_and_same_site_strict()
    {
        var res = await NewClient().TryLoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);
        var cookie = AuthCookie(res);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Fact]
    public async Task Cookies_are_secure_when_configured_for_https()
    {
        await using var https = Factory.WithWebHostBuilder(b => b.UseSetting("Auth:CookieSecurePolicy", "Always"));
        var client = new ApiClient(https.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
        }));
        var res = await client.TryLoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);
        Assert.Contains("secure", AuthCookie(res));
    }

    [Fact]
    public async Task Forwarded_https_from_trusted_proxy_marks_cookie_secure()
    {
        await using var proxied = Factory.WithWebHostBuilder(b => b.UseSetting("ForwardedHeaders:Enabled", "true"));
        var http = proxied.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

        var plain = await http.GetAsync("/api/auth/csrf");
        Assert.DoesNotContain("secure", AntiforgeryCookie(plain));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        request.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Contains("secure", AntiforgeryCookie(await http.SendAsync(request)));
    }

    private static string AntiforgeryCookie(HttpResponseMessage res) =>
        res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mbalms.af=")).ToLowerInvariant();

    private static string AuthCookie(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mbalms.auth=")).ToLowerInvariant();
    }
}
