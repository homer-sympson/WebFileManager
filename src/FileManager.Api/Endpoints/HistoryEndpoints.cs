using FileManager.Api.Auth;
using FileManager.Api.Contracts;
using FileManager.Core.Data;

namespace FileManager.Api.Endpoints;

public static class HistoryEndpoints
{
    public static RouteGroupBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/history").WithTags("History");
        group.MapGet("/", GetAsync);
        group.MapPost("/", RecordAsync);
        group.MapDelete("/", ClearAsync);
        return group;
    }

    private static async Task<IResult> GetAsync(
        HttpContext http,
        ICurrentUserAccessor accessor,
        IHistoryService history,
        int? limit,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        var items = await history.GetAsync(user.DbUserId, limit ?? 100, cancellationToken).ConfigureAwait(false);
        return Results.Ok(items);
    }

    private static async Task<IResult> RecordAsync(
        HttpContext http,
        HistoryRequest request,
        ICurrentUserAccessor accessor,
        IHistoryService history,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        await history.RecordAsync(user.DbUserId, request.Path, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearAsync(
        HttpContext http,
        ICurrentUserAccessor accessor,
        IHistoryService history,
        CancellationToken cancellationToken)
    {
        var user = accessor.Require(http);
        await history.ClearAsync(user.DbUserId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }
}
