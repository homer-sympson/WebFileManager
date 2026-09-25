using FileManager.Api.Auth;
using FileManager.Api.Contracts;
using FileManager.Core.Data;
using FileManager.Core.Linux;
using FileManager.Core.Models;

namespace FileManager.Api.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(AuthorizationPolicies.Admin);

        group.MapGet("/", ListAsync);
        group.MapGet("/groups", Groups);
        group.MapGet("/{name}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{name}", UpdateAsync);
        group.MapDelete("/{name}", DeleteAsync);

        return group;
    }

    private static IResult ListAsync(IHostUserDirectory directory, IPosixAclService acl, bool? includeSystem = false)
    {
        var users = directory.GetAll()
            .Where(u => includeSystem == true || u.IsLoginCapable)
            .OrderBy(u => u.Uid)
            .Select(u => new HostUserResponse(u.Name, u.Uid, u.Gid, u.FullName, u.HomeDirectory, u.Shell, u.GroupNames, u.IsAdmin, u.HasUsablePassword))
            .ToArray();

        return Results.Ok(new HostUserListResponse(users, acl.IsAvailable));
    }

    private static IResult Groups(IHostUserAdminService admin) => Results.Ok(admin.GetExistingGroups());

    private static async Task<IResult> GetAsync(
        string name,
        IHostUserAdminService admin,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var paths = http.Request.Query["paths"].Select(p => p ?? string.Empty).Where(p => p.Length > 0).ToArray();
        var details = await admin.GetAsync(name, paths, cancellationToken).ConfigureAwait(false);
        return Results.Ok(details);
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        IHostUserAdminService admin,
        ICurrentUserAccessor accessor,
        IAuditService audit,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var actor = accessor.Require(http);
        var details = await admin.CreateAsync(
            new CreateHostUserRequest(
                request.UserName,
                request.Password,
                request.FullName,
                request.Shell,
                request.CreateHome,
                request.HomeDirectory,
                request.Groups ?? [],
                request.GrantSudo,
                request.SudoNopasswd,
                MapGrants(request.PathGrants)),
            cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            actor.DbUserId,
            actor.Name,
            "user.create",
            request.UserName,
            $"sudo={request.GrantSudo};groups={string.Join(',', request.Groups ?? [])};paths={string.Join(',', request.PathGrants?.Select(g => g.Path) ?? [])}",
            http.Connection.RemoteIpAddress?.ToString(),
            cancellationToken).ConfigureAwait(false);

        return Results.Created($"/api/users/{details.Name}", details);
    }

    private static async Task<IResult> UpdateAsync(
        string name,
        UpdateUserRequest request,
        IHostUserAdminService admin,
        ICurrentUserAccessor accessor,
        IUserStore userStore,
        ISessionService sessions,
        IAuditService audit,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var actor = accessor.Require(http);
        var details = await admin.UpdateAsync(
            name,
            new UpdateHostUserRequest(
                request.FullName,
                request.Shell,
                request.Password,
                request.Groups,
                request.GrantSudo,
                request.SudoNopasswd,
                request.PathGrants is null ? null : MapGrants(request.PathGrants),
                request.RevokeAclPaths),
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            // A password change closes the sessions of that account; the administrator changing their
            // own password keeps working in the current browser.
            var target = await userStore.FindAsync(name, cancellationToken).ConfigureAwait(false);
            if (target is not null)
            {
                var isSelf = string.Equals(actor.Name, name, StringComparison.Ordinal);
                var except = isSelf ? SessionService.Hash(actor.Token) : null;
                var revoked = await sessions
                    .RevokeAllAsync(target.Id, SessionRevocationReasons.PasswordChanged, except, cancellationToken)
                    .ConfigureAwait(false);

                await audit.WriteAsync(
                    actor.DbUserId,
                    actor.Name,
                    "user.password",
                    name,
                    $"self={isSelf};revokedSessions={revoked}",
                    http.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await audit.WriteAsync(
            actor.DbUserId,
            actor.Name,
            "user.update",
            name,
            $"sudo={request.GrantSudo};groups={string.Join(',', request.Groups ?? [])}",
            http.Connection.RemoteIpAddress?.ToString(),
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(details);
    }

    private static async Task<IResult> DeleteAsync(
        string name,
        IHostUserAdminService admin,
        ICurrentUserAccessor accessor,
        IAuditService audit,
        HttpContext http,
        bool? removeHome,
        CancellationToken cancellationToken)
    {
        var actor = accessor.Require(http);
        if (string.Equals(actor.Name, name, StringComparison.Ordinal))
        {
            throw Core.FileManagerException.BadRequest("Нельзя удалить учётную запись, под которой выполнен вход.");
        }

        var revoke = http.Request.Query["revokePaths"].Select(p => p ?? string.Empty).Where(p => p.Length > 0).ToArray();
        await admin.DeleteAsync(name, removeHome ?? false, revoke, cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            actor.DbUserId,
            actor.Name,
            "user.delete",
            name,
            $"removeHome={removeHome}",
            http.Connection.RemoteIpAddress?.ToString(),
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static IReadOnlyList<PathGrant> MapGrants(IReadOnlyList<PathGrantRequest>? grants) =>
        grants is null
            ? []
            : grants.Select(g => new PathGrant(g.Path, g.Access, g.Default)).ToArray();
}
