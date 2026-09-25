using System.Threading.RateLimiting;
using FileManager.Api.Auth;
using FileManager.Api.Endpoints;
using FileManager.Api.Infrastructure;
using FileManager.Api.Spa;
using FileManager.Core;
using FileManager.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFileManagerCore(builder.Configuration);

builder.Services.AddSingleton<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddSingleton<ILoginThrottle, LoginThrottle>();

builder.Services
    .AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        AuthorizationPolicies.Admin,
        policy => policy.RequireAuthenticatedUser().RequireClaim(FileManagerClaims.IsAdmin, "true"));

    // Everything is private unless an endpoint opts out (login, health, capabilities).
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(
        RateLimitPolicies.Login,
        http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

builder.Services.AddExceptionHandler<FileManagerExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var fileManagerOptions = builder.Configuration.GetSection(FileManagerOptions.SectionName).Get<FileManagerOptions>() ?? new FileManagerOptions();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = fileManagerOptions.Upload.MaxRequestSizeBytes;
    options.MultipartHeadersLengthLimit = 64 * 1024;
    options.ValueLengthLimit = 64 * 1024;
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = fileManagerOptions.Upload.MaxRequestSizeBytes;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<SameOriginGuardMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseStaticFiles();
app.UseMiddleware<SpaFallbackMiddleware>();

app.UseAuthentication();
app.UseMiddleware<TabLeaseMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSystemEndpoints();
app.MapFileSystemEndpoints();
app.MapUserEndpoints();
app.MapSessionEndpoints();
app.MapHistoryEndpoints();

app.Run();

/// <summary>Exposed so integration tests can use WebApplicationFactory.</summary>
public partial class Program;
