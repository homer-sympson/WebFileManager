using System.Collections.Concurrent;
using FileManager.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FileManager.Api.Infrastructure;

/// <summary>In-memory brute force protection for the login endpoint (per client address + account).</summary>
public interface ILoginThrottle
{
    bool IsLocked(string key, out TimeSpan retryAfter);

    void RegisterFailure(string key);

    void Reset(string key);
}

public sealed class LoginThrottle : ILoginThrottle
{
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private readonly AuthOptions _options;

    public LoginThrottle(IOptions<FileManagerOptions> options)
    {
        _options = options.Value.Auth;
    }

    public bool IsLocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_attempts.TryGetValue(key, out var attempt) || attempt.LockedUntil is not { } lockedUntil)
        {
            return false;
        }

        if (lockedUntil <= DateTimeOffset.UtcNow)
        {
            _attempts.TryRemove(key, out _);
            return false;
        }

        retryAfter = lockedUntil - DateTimeOffset.UtcNow;
        return true;
    }

    public void RegisterFailure(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var attempt = _attempts.AddOrUpdate(
            key,
            _ => new Attempt(1, null, now),
            (_, existing) => existing with { Count = existing.Count + 1, UpdatedUtc = now });

        if (_options.MaxFailedAttempts > 0 && attempt.Count >= _options.MaxFailedAttempts)
        {
            _attempts[key] = attempt with { LockedUntil = now.AddMinutes(Math.Max(1, _options.LockoutMinutes)) };
        }

        Cleanup(now);
    }

    public void Reset(string key) => _attempts.TryRemove(key, out _);

    private void Cleanup(DateTimeOffset now)
    {
        if (_attempts.Count < 1024)
        {
            return;
        }

        foreach (var (key, attempt) in _attempts)
        {
            if (attempt.LockedUntil is null && now - attempt.UpdatedUtc > TimeSpan.FromMinutes(30))
            {
                _attempts.TryRemove(key, out _);
            }
        }
    }

    private sealed record Attempt(int Count, DateTimeOffset? LockedUntil, DateTimeOffset UpdatedUtc);
}
