using System.Text;
using FileManager.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Linux;

public interface ISudoersService
{
    string GetPath(string userName);

    Task ApplyAsync(string userName, bool nopasswd, CancellationToken cancellationToken = default);

    Task RemoveAsync(string userName, CancellationToken cancellationToken = default);

    string? Read(string userName);
}

/// <summary>
/// Manages the per user drop-in in /etc/sudoers.d, which is where Linux keeps this kind of setting.
/// Every write is validated with visudo before it becomes visible to sudo.
/// </summary>
public sealed class SudoersService : ISudoersService
{
    private const string Header = "# Managed by FileManager. Do not edit manually.";

    private readonly IProcessRunner _runner;
    private readonly ILogger<SudoersService> _logger;
    private readonly FileManagerOptions _options;

    public SudoersService(IProcessRunner runner, IOptions<FileManagerOptions> options, ILogger<SudoersService> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public string GetPath(string userName) => Path.Combine(_options.Linux.SudoersDir, userName);

    public static string BuildContent(string userName, bool nopasswd)
    {
        var clause = nopasswd ? "NOPASSWD: ALL" : "ALL";
        return $"{Header}{Environment.NewLine}{userName} ALL=(ALL:ALL) {clause}{Environment.NewLine}";
    }

    public async Task ApplyAsync(string userName, bool nopasswd, CancellationToken cancellationToken = default)
    {
        ValidateUserName(userName);
        var directory = _options.Linux.SudoersDir;
        Directory.CreateDirectory(directory);

        var path = GetPath(userName);
        var content = BuildContent(userName, nopasswd);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);

        var validation = await _runner.RunAsync(_options.Linux.Visudo, ["-cf", path], null, cancellationToken).ConfigureAwait(false);
        if (!validation.Success)
        {
            File.Delete(path);
            throw FileManagerException.BadRequest($"Проверка sudoers не пройдена: {validation.CombinedOutput.Trim()}");
        }

        _logger.LogInformation("Sudo rule for {User} written to {Path} (nopasswd={NoPasswd}).", userName, path, nopasswd);
    }

    public Task RemoveAsync(string userName, CancellationToken cancellationToken = default)
    {
        ValidateUserName(userName);
        var path = GetPath(userName);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Sudo rule {Path} removed.", path);
        }

        return Task.CompletedTask;
    }

    public string? Read(string userName)
    {
        var path = GetPath(userName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static void ValidateUserName(string userName)
    {
        if (!UserNameValidator.IsValid(userName))
        {
            throw FileManagerException.BadRequest("Недопустимое имя пользователя.");
        }
    }
}

public static class UserNameValidator
{
    public const int MaxLength = 32;

    public static bool IsValid(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > MaxLength)
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(userName[0]) && userName[0] != '_')
        {
            return false;
        }

        foreach (var c in userName)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}
