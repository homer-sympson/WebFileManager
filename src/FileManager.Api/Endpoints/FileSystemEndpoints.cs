using FileManager.Api.Auth;
using FileManager.Api.Contracts;
using FileManager.Core;
using FileManager.Core.Configuration;
using FileManager.Core.Data;
using FileManager.Core.FileSystem;
using FileManager.Core.Models;
using Microsoft.Extensions.Options;

namespace FileManager.Api.Endpoints;

public static class FileSystemEndpoints
{
    public static RouteGroupBuilder MapFileSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fs").WithTags("Filesystem");

        group.MapGet("/list", ListAsync);
        group.MapGet("/download", DownloadAsync);
        group.MapGet("/archive", ArchiveAsync);
        group.MapPost("/mkdir", MkdirAsync);
        group.MapPost("/rename", RenameAsync);
        group.MapPost("/copy", CopyAsync);
        group.MapPost("/move", MoveAsync);
        group.MapDelete("/entry", DeleteAsync);
        group.MapPost("/upload", UploadAsync).DisableAntiforgery();

        return group;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        string? path,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IHistoryService history,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        var listing = await files.ListAsync(user.Identity, path ?? "/", cancellationToken).ConfigureAwait(false);
        await history.RecordAsync(user.DbUserId, listing.Path, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new DirectoryListingResponse(
            listing.Path,
            listing.ParentPath,
            listing.Entries,
            listing.Truncated,
            listing.TotalEntries));
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext http,
        string? path,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        var resolved = path ?? throw FileManagerException.BadRequest("Не указан путь.");
        var stream = await files.OpenReadAsync(user.Identity, resolved, cancellationToken).ConfigureAwait(false);
        var name = Path.GetFileName(resolved.TrimEnd('/'));

        return Results.File(stream, "application/octet-stream", string.IsNullOrEmpty(name) ? "download" : name, enableRangeProcessing: true);
    }

    private static async Task<IResult> ArchiveAsync(
        HttpContext http,
        string? path,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        var resolved = path ?? throw FileManagerException.BadRequest("Не указан путь.");
        var staged = files.CreateStagingFile();

        try
        {
            await files.WriteArchiveAsync(user.Identity, resolved, staged.Content, cancellationToken).ConfigureAwait(false);
            staged.Content.Flush();
            staged.Content.Position = 0;
        }
        catch
        {
            await staged.Content.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var name = Path.GetFileName(resolved.TrimEnd('/'));
        return Results.File(staged.Content, "application/gzip", $"{(string.IsNullOrEmpty(name) ? "root" : name)}.tar.gz");
    }

    private static async Task<IResult> MkdirAsync(
        HttpContext http,
        MkdirRequest request,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        await files.CreateDirectoryAsync(user.Identity, request.Path, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(user.DbUserId, user.Name, "fs.mkdir", request.Path, null, Ip(http), cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> RenameAsync(
        HttpContext http,
        RenameRequest request,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        await files.RenameAsync(user.Identity, request.Path, request.NewName, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(user.DbUserId, user.Name, "fs.rename", request.Path, $"newName={request.NewName}", Ip(http), cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static Task<IResult> CopyAsync(
        HttpContext http,
        TransferRequest request,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IAuditService audit,
        CancellationToken cancellationToken) =>
        TransferAsync(http, request, move: false, accessor, files, audit, cancellationToken);

    private static Task<IResult> MoveAsync(
        HttpContext http,
        TransferRequest request,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IAuditService audit,
        CancellationToken cancellationToken) =>
        TransferAsync(http, request, move: true, accessor, files, audit, cancellationToken);

    private static async Task<IResult> TransferAsync(
        HttpContext http,
        TransferRequest request,
        bool move,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        var result = move
            ? await files.MoveAsync(user.Identity, request.Sources, request.Destination, request.Conflict, cancellationToken).ConfigureAwait(false)
            : await files.CopyAsync(user.Identity, request.Sources, request.Destination, request.Conflict, cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            user.DbUserId,
            user.Name,
            move ? "fs.move" : "fs.copy",
            request.Destination,
            $"sources={string.Join(',', request.Sources)};affected={result.Affected.Count};skipped={result.Skipped.Count}",
            Ip(http),
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new { affected = result.Affected, skipped = result.Skipped });
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext http,
        string? path,
        bool? recursive,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        var resolved = path ?? throw FileManagerException.BadRequest("Не указан путь.");
        await files.DeleteAsync(user.Identity, resolved, recursive ?? false, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(user.DbUserId, user.Name, "fs.delete", resolved, $"recursive={recursive}", Ip(http), cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http,
        IFormFile file,
        string? path,
        bool? overwrite,
        ICurrentUserAccessor accessor,
        IFileSystemService files,
        IPathPolicy paths,
        IOptions<FileManagerOptions> options,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);

        if (file.Length == 0 && file.Name.Length == 0)
        {
            throw FileManagerException.BadRequest("Файл не передан.");
        }

        if (options.Value.Upload.MaxFileSizeBytes > 0 && file.Length > options.Value.Upload.MaxFileSizeBytes)
        {
            throw FileManagerException.BadRequest($"Файл больше допустимого размера ({options.Value.Upload.MaxFileSizeBytes} байт).");
        }

        var baseDirectory = paths.Resolve(path ?? "/");
        var relative = http.Request.HasFormContentType && http.Request.Form.TryGetValue("relativePath", out var value)
            ? value.ToString()
            : string.Empty;

        var fileName = string.IsNullOrWhiteSpace(relative) ? file.FileName : relative;
        fileName = fileName.Replace('\\', '/').TrimStart('/');

        if (fileName.Contains("..", StringComparison.Ordinal))
        {
            throw FileManagerException.BadRequest("Недопустимый относительный путь.");
        }

        var createParents = fileName.Contains('/', StringComparison.Ordinal);
        var destination = paths.Resolve(Path.Combine(baseDirectory, fileName));

        var staged = files.CreateStagingFile();
        string committed;
        try
        {
            await file.CopyToAsync(staged.Content, cancellationToken).ConfigureAwait(false);
            staged.Content.Flush();
            staged.Content.Position = 0;
            committed = await files.CommitUploadAsync(
                user.Identity,
                destination,
                staged.Content,
                overwrite ?? true,
                createParents,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await staged.Content.DisposeAsync().ConfigureAwait(false);
        }

        await audit.WriteAsync(user.DbUserId, user.Name, "fs.upload", committed, $"size={file.Length}", Ip(http), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { path = committed, size = file.Length });
    }

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
