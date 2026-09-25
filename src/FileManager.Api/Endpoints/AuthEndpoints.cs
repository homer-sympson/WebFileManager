using FileManager.Api.Auth;
using FileManager.Api.Contracts;
using FileManager.Api.Infrastructure;
using FileManager.Core;
using FileManager.Core.Configuration;
using FileManager.Core.Data;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.Extensions.Options;
namespace FileManager.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Login);

        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", Me);
        group.MapGet("/login-options", LoginOptions).AllowAnonymous();
        group.MapPost("/page-closed", PageClosedAsync).AllowAnonymous();

        return group;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        LoginRequest request,
        IHostUserDirectory directory,
        IHostAuthenticator authenticator,
        IUserStore users,
        ISessionService sessions,
        ISudoersService sudoers,
        ILoginThrottle throttle,
        IAuditService audit,
        IOptions<FileManagerOptions> options,
        CancellationToken cancellationToken)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        var remoteIp = http.Connection.RemoteIpAddress?.ToString();
        var throttleKey = $"{remoteIp}|{userName}";

        if (userName.Length == 0 || string.IsNullOrEmpty(request.Password))
        {
            return Unauthorized();
        }

        if (throttle.IsLocked(throttleKey, out var retryAfter))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Слишком много попыток",
                detail: $"Вход заблокирован. Повторите попытку через {Math.Ceiling(retryAfter.TotalMinutes)} мин.");
        }

        var hostUser = directory.Find(userName);
        if (hostUser is null || !hostUser.IsLoginCapable)
        {
            // The same answer for unknown, password-less and locked accounts: no user enumeration.
            throttle.RegisterFailure(throttleKey);
            await audit.WriteAsync(null, userName, "login.failed", null, "account cannot log in", remoteIp, cancellationToken).ConfigureAwait(false);
            return Unauthorized();
        }

        var outcome = await authenticator.AuthenticateAsync(hostUser.Name, request.Password, cancellationToken).ConfigureAwait(false);
        if (!outcome.Success)
        {
            throttle.RegisterFailure(throttleKey);
            await audit.WriteAsync(null, hostUser.Name, "login.failed", null, outcome.FailureReason, remoteIp, cancellationToken).ConfigureAwait(false);
            return Unauthorized();
        }

        throttle.Reset(throttleKey);

        var dbUser = await users.GetOrCreateAsync(hostUser.Name, hostUser.Uid, hostUser.IsAdmin, cancellationToken).ConfigureAwait(false);

        // One account, one browser: a live session is never silently replaced, the new sign-in is refused.
        SessionCreated session;
        try
        {
            session = await sessions.CreateAsync(dbUser, remoteIp, http.Request.Headers.UserAgent.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (AlreadySignedInException conflict)
        {
            await audit.WriteAsync(
                dbUser.Id,
                hostUser.Name,
                "login.refused",
                null,
                $"alreadySignedInSince={conflict.LastSeenUtc:O}",
                remoteIp,
                cancellationToken).ConfigureAwait(false);

            return Results.Problem(
                title: "Уже выполнен вход",
                detail: SessionMessages.AlreadySignedIn(conflict.LastSeenUtc),
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = AlreadySignedInException.Code,
                    ["lastSeenUtc"] = conflict.LastSeenUtc,
                });
        }

        WriteSessionCookie(http, options.Value, session);

        await audit.WriteAsync(
            dbUser.Id,
            hostUser.Name,
            "login",
            null,
            $"provider={outcome.Provider};revokedSessions={session.RevokedSessions}",
            remoteIp,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(BuildProfile(hostUser, sudoers));
    }

    /// <summary>
    /// Called by `navigator.sendBeacon` when the page is closed. The session is not revoked at once:
    /// a reload within the grace window cancels the close, afterwards the session counts as ended and
    /// the account becomes available for a sign-in from another browser.
    /// </summary>
    private static async Task<IResult> PageClosedAsync(
        HttpContext http,
        ISessionService sessions,
        IOptions<FileManagerOptions> options,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var token = http.Request.Cookies[options.Value.Auth.CookieName];
        if (!string.IsNullOrEmpty(token))
        {
            var tabId = http.Request.Query["tabId"].ToString();
            await sessions.MarkPageClosedAsync(token, string.IsNullOrWhiteSpace(tabId) ? null : tabId, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(
                null,
                null,
                "session.page-closed",
                null,
                $"tab={tabId}",
                http.Connection.RemoteIpAddress?.ToString(),
                cancellationToken).ConfigureAwait(false);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        ISessionService sessions,
        ICurrentUserAccessor accessor,
        IAuditService audit,
        IOptions<FileManagerOptions> options,
        CancellationToken cancellationToken)
    {
        var current = accessor.Get(http);
        var token = current?.Token ?? http.Request.Cookies[options.Value.Auth.CookieName];
        if (!string.IsNullOrEmpty(token))
        {
            await sessions.RevokeAsync(token, SessionRevocationReasons.SignedOut, cancellationToken).ConfigureAwait(false);
        }

        if (current is not null)
        {
            await audit.WriteAsync(current.DbUserId, current.Name, "logout", null, null, http.Connection.RemoteIpAddress?.ToString(), cancellationToken).ConfigureAwait(false);
        }

        http.Response.Cookies.Delete(options.Value.Auth.CookieName, new CookieOptions { Path = "/" });
        return Results.NoContent();
    }

    private static IResult Me(HttpContext http, ICurrentUserAccessor accessor, IHostUserDirectory directory, ISudoersService sudoers)
    {
        var current = accessor.Get(http);
        var hostUser = current is null ? null : directory.Find(current.Name);
        if (current is null || hostUser is null)
        {
            return Unauthorized();
        }

        return Results.Ok(BuildProfile(hostUser, sudoers));
    }

    private static IResult LoginOptions(IHostUserDirectory directory, IOptions<FileManagerOptions> options)
    {
        var expose = options.Value.Auth.ExposeUserList;
        var names = expose
            ? directory.GetAll().Where(u => u.IsLoginCapable).Select(u => u.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : [];

        return Results.Ok(new LoginOptionsResponse(expose, names));
    }

    private static UserProfileResponse BuildProfile(HostUser user, ISudoersService sudoers) =>
        new(
            user.Name,
            user.Uid,
            user.Gid,
            user.FullName,
            user.HomeDirectory,
            user.IsAdmin,
            sudoers.Read(user.Name) is not null,
            user.GroupNames);

    private static void WriteSessionCookie(HttpContext http, FileManagerOptions options, SessionCreated session)
    {
        http.Response.Cookies.Append(
            options.Auth.CookieName,
            session.Token,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = options.Auth.RequireSecureCookie || http.Request.IsHttps,
                Path = "/",
                Expires = session.ExpiresUtc,
            });
    }

    private static IResult Unauthorized() =>
        Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Требуется вход",
            detail: "Неверное имя пользователя или пароль.");
}
