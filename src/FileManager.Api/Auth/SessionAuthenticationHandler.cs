using System.Security.Claims;
using FileManager.Api.Infrastructure;
using FileManager.Core;
using FileManager.Core.Configuration;
using FileManager.Core.Data;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FileManager.Api.Auth;

public static class FileManagerClaims
{
    public const string UserId = "fm:uid";
    public const string LinuxUid = "fm:linux-uid";
    public const string IsAdmin = "fm:admin";
    public const string Token = "fm:token";
}

public sealed record CurrentUser(string Name, int DbUserId, LinuxIdentity Identity, bool IsAdmin, string Token);

public interface ICurrentUserAccessor
{
    CurrentUser? Get(HttpContext context);

    CurrentUser Require(HttpContext context);
}

public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHostUserDirectory _directory;

    public CurrentUserAccessor(IHostUserDirectory directory)
    {
        _directory = directory;
    }

    public CurrentUser? Get(HttpContext context)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var name = principal.Identity.Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var hostUser = _directory.Find(name);
        if (hostUser is null)
        {
            return null;
        }

        var dbUserId = int.TryParse(principal.FindFirst(FileManagerClaims.UserId)?.Value, out var id) ? id : 0;
        var token = principal.FindFirst(FileManagerClaims.Token)?.Value ?? string.Empty;

        return new CurrentUser(hostUser.Name, dbUserId, hostUser.ToIdentity(), hostUser.IsAdmin, token);
    }

    public CurrentUser Require(HttpContext context) =>
        Get(context) ?? throw FileManagerException.Unauthorized("Требуется вход в систему.");
}

/// <summary>Validates the opaque session cookie against the session table in PostgreSQL.</summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "FileManagerSession";

    private readonly ISessionService _sessions;
    private readonly IHostUserDirectory _directory;
    private readonly AuthOptions _options;
    private string? _failureReasonCode;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISessionService sessions,
        IHostUserDirectory directory,
        IOptions<FileManagerOptions> fileManagerOptions)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _directory = directory;
        _options = fileManagerOptions.Value.Auth;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(_options.CookieName, out var token) || string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var tabId = Request.Headers[TabLease.HeaderName].ToString();
        var validation = await _sessions.ValidateAsync(token, string.IsNullOrWhiteSpace(tabId) ? null : tabId, Context.RequestAborted).ConfigureAwait(false);

        if (validation.User is null)
        {
            _failureReasonCode = validation.ReasonCode;
            return AuthenticateResult.Fail(SessionMessages.Describe(validation.ReasonCode));
        }

        var hostUser = _directory.Find(validation.User.UserName);
        if (hostUser is null || !hostUser.IsLoginCapable)
        {
            // The account disappeared or lost its password: kill the session immediately.
            await _sessions.RevokeAsync(token, SessionRevocationReasons.AccountUnusable, Context.RequestAborted).ConfigureAwait(false);
            _failureReasonCode = SessionRevocationReasons.AccountUnusable;
            return AuthenticateResult.Fail(SessionMessages.Describe(SessionRevocationReasons.AccountUnusable));
        }

        if (validation.TabConflict)
        {
            Context.Items[TabLease.ConflictItemKey] = true;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, hostUser.Name),
            new(FileManagerClaims.UserId, validation.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(FileManagerClaims.LinuxUid, hostUser.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(FileManagerClaims.IsAdmin, hostUser.IsAdmin ? "true" : "false"),
            new(FileManagerClaims.Token, token),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <summary>
    /// Reports why the caller is unauthenticated as an RFC 7807 document so the client can tell
    /// "never signed in" apart from "signed in somewhere else, this session was closed".
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!Request.Path.StartsWithSegments("/api"))
        {
            await base.HandleChallengeAsync(properties).ConfigureAwait(false);
            return;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(
            new
            {
                title = "Требуется вход",
                status = StatusCodes.Status401Unauthorized,
                detail = SessionMessages.Describe(_failureReasonCode),
                code = _failureReasonCode ?? SessionMessages.UnauthenticatedCode,
            },
            Context.RequestAborted).ConfigureAwait(false);
    }
}
