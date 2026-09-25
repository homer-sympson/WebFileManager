using FileManager.Api.Auth;
using FileManager.Core;
using FileManager.Core.Data;

namespace FileManager.Api.Endpoints;

/// <summary>
/// Administrator view of active sessions. A browser that crashed or was closed without a `pagehide`
/// event leaves its session behind, and only the account owner can normally end it — so members of
/// the root/admin group (uid 0 or an admin group) can close any session explicitly.
/// </summary>
public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions").RequireAuthorization(AuthorizationPolicies.Admin);

        group.MapGet("/", ListAsync);
        group.MapDelete("/{userName}", CloseAsync);

        return group;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        ISessionService sessions,
        ICurrentUserAccessor accessor,
        CancellationToken cancellationToken)
    {
        var actor = accessor.Require(http);
        var live = await sessions.GetActiveAsync(cancellationToken).ConfigureAwait(false);

        // At most one live session per user, so matching the names identifies the caller's own session.
        var payload = live
            .Select(session => new
            {
                userId = session.UserId,
                userName = session.UserName,
                createdUtc = session.CreatedUtc,
                lastSeenUtc = session.LastSeenUtc,
                expiresUtc = session.ExpiresUtc,
                remoteIp = session.RemoteIp,
                userAgent = session.UserAgent,
                hasActiveTab = session.ActiveTabId is not null,
                pendingPageClose = session.PendingPageClose,
                isCurrentSession = string.Equals(session.UserName, actor.Name, StringComparison.Ordinal),
            })
            .ToArray();

        return Results.Ok(new { sessions = payload });
    }

    private static async Task<IResult> CloseAsync(
        string userName,
        HttpContext http,
        ISessionService sessions,
        IUserStore users,
        ICurrentUserAccessor accessor,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var actor = accessor.Require(http);
        var target = await users.FindAsync(userName, cancellationToken).ConfigureAwait(false)
            ?? throw FileManagerException.NotFound($"Пользователь {userName} не найден в базе приложения.");

        var revoked = await sessions
            .RevokeAllAsync(target.Id, SessionRevocationReasons.ClosedByAdmin, exceptTokenHash: null, cancellationToken)
            .ConfigureAwait(false);

        if (revoked == 0)
        {
            throw FileManagerException.NotFound($"У пользователя {userName} нет активной сессии.");
        }

        await audit.WriteAsync(
            actor.DbUserId,
            actor.Name,
            "session.admin-closed",
            userName,
            $"closedSessions={revoked};self={string.Equals(actor.Name, userName, StringComparison.Ordinal)}",
            http.Connection.RemoteIpAddress?.ToString(),
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }
}
