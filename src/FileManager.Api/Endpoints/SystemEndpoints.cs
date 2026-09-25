using System.Runtime.InteropServices;
using FileManager.Api.Contracts;
using FileManager.Core.Configuration;
using FileManager.Core.Data;
using FileManager.Core.FileSystem;
using FileManager.Core.Linux;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileManager.Api.Endpoints;

public static class SystemEndpoints
{
    public static RouteGroupBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system").WithTags("System");
        group.MapGet("/capabilities", CapabilitiesAsync).AllowAnonymous();
        return group;
    }

    private static async Task<IResult> CapabilitiesAsync(
        IOptions<FileManagerOptions> options,
        IImpersonationExecutor impersonation,
        IPosixAclService acl,
        IPathPolicy paths,
        IDbContextFactory<FileManagerDbContext> dbFactory,
        CancellationToken cancellationToken)
    {
        var databaseReady = true;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            databaseReady = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            databaseReady = false;
        }

        return Results.Ok(new CapabilitiesResponse(
            RuntimeInformation.OSDescription,
            Environment.IsPrivilegedProcess,
            impersonation.IsEnabled,
            PamAuthenticator.IsLibraryAvailable && PamAuthenticator.IsServiceConfigured(options.Value.Auth.PamService),
            File.Exists(options.Value.Linux.Shadow),
            acl.IsAvailable,
            databaseReady,
            paths.Roots));
    }
}
