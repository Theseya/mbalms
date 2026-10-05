using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MbaLms.Api.Tests.Infrastructure;

/// <summary>HTTP client with its own cookie jar and antiforgery handling, like a browser session.</summary>
public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public HttpClient Http { get; } = http;

    public async Task RefreshCsrfAsync()
    {
        var res = await Http.GetFromJsonAsync<JsonElement>("/api/auth/csrf", Json);
        Http.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        Http.DefaultRequestHeaders.Add("X-XSRF-TOKEN", res.GetProperty("token").GetString());
    }

    public async Task<HttpResponseMessage> TryLoginAsync(string email, string password)
    {
        await RefreshCsrfAsync();
        var res = await Http.PostAsJsonAsync("/api/auth/login", new { email, password }, Json);
        await RefreshCsrfAsync();
        return res;
    }

    public async Task LoginAsync(string email, string password)
    {
        var res = await TryLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    public Task<HttpResponseMessage> GetAsync(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => Http.PostAsJsonAsync(url, body ?? new { }, Json);
    public Task<HttpResponseMessage> PutAsync(string url, object body) => Http.PutAsJsonAsync(url, body, Json);
    public Task<HttpResponseMessage> DeleteAsync(string url) => Http.DeleteAsync(url);

    public async Task<T> GetJsonAsync<T>(string url)
    {
        var res = await Http.GetAsync(url);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task<T> PostJsonAsync<T>(string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await PostAsync(url, body);
        await res.EnsureStatusAsync(expected);
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task<T> PutJsonAsync<T>(string url, object body)
    {
        var res = await PutAsync(url, body);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }
}

/// <summary>Field errors of a response; a missing field fails with the whole response body for diagnosis.</summary>
public sealed class FieldErrors(Dictionary<string, string> errors, string body)
{
    public string this[string field] =>
        errors.TryGetValue(field, out var code) ? code : throw new Xunit.Sdk.XunitException($"No error for '{field}' in {body}");
}

public static class HttpResponseExtensions
{
    public static async Task EnsureStatusAsync(this HttpResponseMessage res, HttpStatusCode expected)
    {
        if (res.StatusCode != expected)
        {
            var body = await res.Content.ReadAsStringAsync();
            Assert.Fail($"Expected {(int)expected} but got {(int)res.StatusCode}: {body}");
        }
    }

    /// <summary>Field name → first error code from a validation problem response.</summary>
    public static async Task<FieldErrors> FieldErrorsAsync(this HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        if (!json.TryGetProperty("errors", out var errors)) Assert.Fail($"No field errors in {(int)res.StatusCode}: {body}");
        return new FieldErrors(errors.EnumerateObject().ToDictionary(p => p.Name, p => p.Value[0].GetString()!), body);
    }

    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage res)
    {
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
