namespace FileManager.Core;

/// <summary>An expected failure that carries the HTTP status the API should report.</summary>
public sealed class FileManagerException : Exception
{
    public FileManagerException(int statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    public static FileManagerException BadRequest(string message) => new(HttpStatus.BadRequest, message);

    public static FileManagerException Unauthorized(string message) => new(HttpStatus.Unauthorized, message);

    public static FileManagerException Forbidden(string message) => new(HttpStatus.Forbidden, message);

    public static FileManagerException NotFound(string message) => new(HttpStatus.NotFound, message);

    public static FileManagerException Conflict(string message) => new(HttpStatus.Conflict, message);

    public static FileManagerException NotSupported(string message) => new(HttpStatus.NotImplemented, message);

    public static FileManagerException Failed(string message, Exception? inner = null) => new(HttpStatus.InternalServerError, message, inner);
}

public static class HttpStatus
{
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int NotFound = 404;
    public const int Conflict = 409;
    public const int InternalServerError = 500;
    public const int NotImplemented = 501;
    public const int InsufficientStorage = 507;
}
