using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FileManager.Api.Tests;

public static class ApiTestExtensions
{
    public const string TabHeaderName = "X-Tab-Id";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateApiClient(this ApiFactory factory, bool handleCookies = true) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = handleCookies,
        });

    /// <summary>A client that presents an explicit session cookie and tab identifier.</summary>
    public static HttpClient CreateSessionClient(this ApiFactory factory, string token, string? tabId = null)
    {
        var client = factory.CreateApiClient(handleCookies: false);
        client.DefaultRequestHeaders.Add("Cookie", $"fm_session={token}");
        if (tabId is not null)
        {
            client.DefaultRequestHeaders.Add(TabHeaderName, tabId);
        }

        return client;
    }

    /// <summary>Logs in and returns the raw session token (the cookie value).</summary>
    public static async Task<string> LoginForTokenAsync(this HttpClient client, string userName, string password = "secret123")
    {
        var response = await client.PostJsonAsync("/api/auth/login", new { userName, password });
        response.EnsureSuccessStatusCode();

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        var entry = cookies
            .Select(c => c.Split(';')[0])
            .FirstOrDefault(c => c.StartsWith("fm_session=", StringComparison.Ordinal));

        Assert.False(string.IsNullOrEmpty(entry), "login did not return a session cookie");
        return entry!["fm_session=".Length..];
    }

    /// <summary>Logs in and leaves the session cookie on the client.</summary>
    public static async Task<JsonElement> LoginAsync(this HttpClient client, string userName, string password = "secret123")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { userName, password }),
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> DeleteApiAsync(this HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return client.SendAsync(request);
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);
}
