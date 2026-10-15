using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class HealthTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Health_reports_ok_without_naming_the_database()
    {
        var res = await NewClient().Http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Single(json.EnumerateObject());
    }

    [Fact]
    public async Task Ready_reports_ready_when_the_configured_database_accepts_connections()
    {
        var res = await NewClient().Http.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", json.GetProperty("status").GetString());
        Assert.Single(json.EnumerateObject());
    }
}
