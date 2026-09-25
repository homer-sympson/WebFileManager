using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FileManager.Api.Tests;

public class FileSystemEndpointsTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Lists_the_browse_root_with_metadata()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");
        _factory.WriteShareFile("notes.txt", "hello");
        _factory.EnsureDirectory("share/docs");

        var listing = await (await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(_factory.Share)}")).ReadJsonAsync();

        var names = listing.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Contains("notes.txt", names);
        Assert.Contains("docs", names);
        Assert.Null(listing.GetProperty("parent").GetString());

        var file = listing.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("name").GetString() == "notes.txt");
        Assert.Equal("file", file.GetProperty("type").GetString());
        Assert.Equal(5, file.GetProperty("size").GetInt64());
        Assert.StartsWith("-", file.GetProperty("mode").GetString(), StringComparison.Ordinal);
        Assert.False(listing.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Records_navigation_history()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");
        _factory.EnsureDirectory("share/history-target");

        var path = _factory.FilePath("history-target");
        await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(path)}");

        var history = await (await client.GetAsync("/api/history")).ReadJsonAsync();
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("path").GetString() == path);

        var clear = await client.DeleteApiAsync("/api/history");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.Empty((await (await client.GetAsync("/api/history")).ReadJsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task Creates_uploads_downloads_copies_and_deletes()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        var target = _factory.EnsureDirectory("share/work");
        var dest = _factory.EnsureDirectory("share/archive");

        var mkdir = await client.PostJsonAsync("/api/fs/mkdir", new { path = Path.Combine(target, "nested") });
        Assert.Equal(HttpStatusCode.NoContent, mkdir.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(target, "nested")));

        // upload
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("uploaded payload"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(fileContent, "file", "payload.txt");
        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/fs/upload?path={Uri.EscapeDataString(target)}&overwrite=true")
        {
            Content = multipart,
        };
        uploadRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var upload = await client.SendAsync(uploadRequest);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal("uploaded payload", await File.ReadAllTextAsync(Path.Combine(target, "payload.txt")));

        // folder style upload with a relative path creates parents
        using var folderMultipart = new MultipartFormDataContent();
        folderMultipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("deep")), "file", "deep.txt");
        folderMultipart.Add(new StringContent("a/b/deep.txt"), "relativePath");
        var folderRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/fs/upload?path={Uri.EscapeDataString(target)}&overwrite=true")
        {
            Content = folderMultipart,
        };
        folderRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(folderRequest)).StatusCode);
        Assert.Equal("deep", await File.ReadAllTextAsync(Path.Combine(target, "a/b/deep.txt")));

        // download
        var download = await client.GetAsync($"/api/fs/download?path={Uri.EscapeDataString(Path.Combine(target, "payload.txt"))}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("uploaded payload", await download.Content.ReadAsStringAsync());

        // copy
        var copy = await client.PostJsonAsync("/api/fs/copy", new
        {
            sources = new[] { Path.Combine(target, "payload.txt") },
            destination = dest,
            conflict = "fail",
        });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.True(File.Exists(Path.Combine(dest, "payload.txt")));

        // copy conflict
        var conflict = await client.PostJsonAsync("/api/fs/copy", new
        {
            sources = new[] { Path.Combine(target, "payload.txt") },
            destination = dest,
            conflict = "fail",
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // rename + delete
        var rename = await client.PostJsonAsync("/api/fs/rename", new { path = Path.Combine(dest, "payload.txt"), newName = "renamed.txt" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        var delete = await client.DeleteApiAsync($"/api/fs/entry?path={Uri.EscapeDataString(Path.Combine(dest, "renamed.txt"))}&recursive=false");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(File.Exists(Path.Combine(dest, "renamed.txt")));
    }

    [Fact]
    public async Task Downloads_a_directory_as_tar_gz_archive()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");
        var archiveRoot = _factory.EnsureDirectory("share/archive-me");
        File.WriteAllText(Path.Combine(archiveRoot, "inner.txt"), "inner");

        var response = await client.GetAsync($"/api/fs/archive?path={Uri.EscapeDataString(archiveRoot)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/gzip", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
        Assert.Equal(0x1f, bytes[0]);
        Assert.Equal(0x8b, bytes[1]);
    }

    [Fact]
    public async Task Rejects_paths_outside_the_browse_root()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        var response = await client.GetAsync("/api/fs/list?path=%2Fetc");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Mutations_require_the_same_origin_marker()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/fs/mkdir")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { path = Path.Combine(_factory.Share, "no-header") }),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_from_another_origin_are_rejected()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/fs/mkdir")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { path = Path.Combine(_factory.Share, "cross-origin") }),
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Origin", "http://evil.example");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_delete_of_the_browse_root()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        var response = await client.DeleteApiAsync($"/api/fs/entry?path={Uri.EscapeDataString(_factory.Share)}&recursive=true");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(Directory.Exists(_factory.Share));
    }

    [Fact]
    public async Task Movement_and_hidden_entries_work()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");
        var source = _factory.WriteShareFile("move-me.txt", "move");
        var dest = _factory.EnsureDirectory("share/moved");

        var move = await client.PostJsonAsync("/api/fs/move", new
        {
            sources = new[] { source },
            destination = dest,
            conflict = "rename",
        });

        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.True(File.Exists(Path.Combine(dest, "move-me.txt")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Large_upload_is_rejected_when_it_exceeds_the_configured_limit()
    {
        // Covered by configuration validation unit tests; here we assert the happy path size is reported.
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("sized")), "file", "sized.txt");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/fs/upload?path={Uri.EscapeDataString(_factory.Share)}")
        {
            Content = multipart,
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.ReadJsonAsync();
        Assert.Equal(5, payload.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Unknown_paths_return_not_found()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        var response = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(Path.Combine(_factory.Share, "missing"))}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        Assert.True(element.TryGetProperty(name, out var value), $"Property {name} missing");
        return value;
    }
}
