using FileManager.Core;

namespace FileManager.Api.Infrastructure;

/// <summary>
/// Cookie based sessions plus same-origin SPA: every state changing API call must come from
/// the same origin and carry the X-Requested-With marker that browsers only send for same-site XHR.
/// </summary>
public sealed class SameOriginGuardMiddleware
{
    private static readonly string[] MutatingMethods = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Endpoints that must stay reachable from a closing page (`navigator.sendBeacon` cannot set
    /// headers). They are idempotent and only ever end the caller's own session, and the Origin
    /// check below still applies.
    /// </summary>
    private static readonly string[] BeaconPaths = ["/api/auth/logout", "/api/auth/page-closed"];

    private readonly RequestDelegate _next;

    public SameOriginGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api") &&
            MutatingMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) &&
                !string.Equals(origin.TrimEnd('/'), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
            {
                throw FileManagerException.Forbidden("Запрос с другого origin отклонён.");
            }

            var isBeacon = BeaconPaths.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!isBeacon && context.Request.Cookies.Count > 0 && !context.Request.Headers.ContainsKey("X-Requested-With"))
            {
                throw FileManagerException.Forbidden("Отсутствует обязательный заголовок X-Requested-With.");
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
