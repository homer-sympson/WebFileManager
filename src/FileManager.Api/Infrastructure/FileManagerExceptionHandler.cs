using FileManager.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FileManager.Api.Infrastructure;

/// <summary>Turns domain failures into RFC 7807 responses with a user facing message.</summary>
public sealed class FileManagerExceptionHandler : IExceptionHandler
{
    private readonly ILogger<FileManagerExceptionHandler> _logger;

    public FileManagerExceptionHandler(ILogger<FileManagerExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var status = exception switch
        {
            FileManagerException managerException => managerException.StatusCode,
            UnauthorizedAccessException => HttpStatus.Forbidden,
            FileNotFoundException or DirectoryNotFoundException => HttpStatus.NotFound,
            IOException io when io.HResult == unchecked((int)0x80070070) => HttpStatus.InsufficientStorage,
            IOException => HttpStatus.Conflict,
            _ => HttpStatus.InternalServerError,
        };

        if (status >= 500)
        {
            _logger.LogError(exception, "Unhandled failure on {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogInformation("Request {Method} {Path} failed with {Status}: {Message}", httpContext.Request.Method, httpContext.Request.Path, status, exception.Message);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                HttpStatus.BadRequest => "Некорректный запрос",
                HttpStatus.Unauthorized => "Требуется вход",
                HttpStatus.Forbidden => "Доступ запрещён",
                HttpStatus.NotFound => "Не найдено",
                HttpStatus.Conflict => "Конфликт",
                HttpStatus.NotImplemented => "Не поддерживается",
                HttpStatus.InsufficientStorage => "Недостаточно места",
                _ => "Внутренняя ошибка",
            },
            Detail = status >= 500 && exception is not FileManagerException
                ? "Внутренняя ошибка сервера."
                : exception.Message,
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
