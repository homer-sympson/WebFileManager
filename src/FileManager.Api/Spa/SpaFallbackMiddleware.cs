namespace FileManager.Api.Spa;

/// <summary>
/// Serves the localized Angular bundles: "/" and un-prefixed deep links are redirected to the
/// preferred locale, and client side routes fall back to the matching index.html.
/// </summary>
public sealed class SpaFallbackMiddleware
{
    public const string LanguageCookie = "fm_lang";

    private static readonly string[] SupportedLanguages = ["ru", "en"];

    private readonly RequestDelegate _next;
    private readonly string _webRoot;
    private readonly ILogger<SpaFallbackMiddleware> _logger;

    public SpaFallbackMiddleware(RequestDelegate next, IWebHostEnvironment environment, ILogger<SpaFallbackMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is not null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        if (path is "/" or "")
        {
            context.Response.Redirect($"/{PreferredLanguage(context)}/{context.Request.QueryString}");
            return;
        }

        var language = MatchLanguage(path);
        if (language is null)
        {
            if (FileExists(path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (AcceptsHtml(context))
            {
                context.Response.Redirect($"/{PreferredLanguage(context)}{path}{context.Request.QueryString}");
                return;
            }

            // Anything else is a missing asset: answer 404 here instead of falling through to the
            // authorization middleware, which would turn it into a 401.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (FileExists(path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!AcceptsHtml(context))
        {
            // A missing asset must stay a 404 rather than being answered with index.html.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var index = Path.Combine(_webRoot, language, "index.html");
        if (!File.Exists(index))
        {
            _logger.LogWarning("SPA bundle for locale {Language} was not found at {Path}.", language, index);
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        await context.Response.SendFileAsync(index).ConfigureAwait(false);
    }

    public static string PreferredLanguage(HttpContext context)
    {
        var cookie = context.Request.Cookies[LanguageCookie];
        if (cookie is not null && SupportedLanguages.Contains(cookie, StringComparer.OrdinalIgnoreCase))
        {
            return cookie.ToLowerInvariant();
        }

        foreach (var header in context.Request.Headers.AcceptLanguage)
        {
            if (header is null)
            {
                continue;
            }

            foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tag = part.Split(';')[0].Trim();
                if (tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                {
                    return "ru";
                }

                if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    return "en";
                }
            }
        }

        return "ru";
    }

    private static string? MatchLanguage(string path)
    {
        foreach (var language in SupportedLanguages)
        {
            if (path.StartsWith($"/{language}/", StringComparison.Ordinal) || path.Equals($"/{language}", StringComparison.Ordinal))
            {
                return language;
            }
        }

        return null;
    }

    private static bool AcceptsHtml(HttpContext context) =>
        context.Request.Headers.Accept.Any(value => value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase));

    private bool FileExists(string path)
    {
        try
        {
            var candidate = Path.Combine(_webRoot, path.TrimStart('/'));
            var full = Path.GetFullPath(candidate);
            return full.StartsWith(_webRoot, StringComparison.Ordinal) && File.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

public static class SpaApplicationBuilderExtensions
{
    public static IApplicationBuilder UseFileManagerSpa(this IApplicationBuilder app) =>
        app.UseMiddleware<SpaFallbackMiddleware>();
}
