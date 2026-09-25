using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FileManager.Core.Configuration;
using FileManager.Core.Interop;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Linux;

/// <summary>Runs a synchronous delegate with the credentials of a host user.</summary>
public interface IImpersonationExecutor
{
    bool IsEnabled { get; }

    ValueTask<T> RunAsync<T>(LinuxIdentity identity, Func<T> action, CancellationToken cancellationToken = default);

    Task RunAsync(LinuxIdentity identity, Action action, CancellationToken cancellationToken = default);

    /// <summary>Verifies credential switching really works; throws when it does not.</summary>
    void VerifyCredentialSwitch();
}

internal sealed class WorkItem
{
    public required LinuxIdentity Identity { get; init; }

    public required Func<object?> Action { get; init; }

    public required TaskCompletionSource<object?> Completion { get; init; }
}

/// <summary>
/// Credential switching happens on dedicated threads (never the thread pool) because
/// setresuid/setresgid/setgroups only affect the calling thread on glibc &gt;= 2.24.
/// </summary>
public sealed class LinuxImpersonationExecutor : IImpersonationExecutor, IDisposable
{
    private const uint RootId = 0;

    [ThreadStatic]
    private static bool _insideScope;

    private readonly Worker[] _workers;
    private readonly ILogger<LinuxImpersonationExecutor> _logger;
    private readonly ImpersonationOptions _options;
    private int _cursor;

    public LinuxImpersonationExecutor(
        IOptions<FileManagerOptions> options,
        ILogger<LinuxImpersonationExecutor> logger)
    {
        _options = options.Value.Impersonation;
        _logger = logger;

        var count = _options.Threads > 0
            ? _options.Threads
            : Math.Clamp(Environment.ProcessorCount, 2, 8);

        _workers = new Worker[count];
        for (var i = 0; i < count; i++)
        {
            _workers[i] = new Worker(i, RunWorker);
        }
    }

    public bool IsEnabled => true;

    public ValueTask<T> RunAsync<T>(LinuxIdentity identity, Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (identity.IsAdmin || identity.IsRoot)
        {
            // Administrative users keep the API process credentials (root).
            return new ValueTask<T>(RunAsRoot(action));
        }

        if (_insideScope)
        {
            throw new InvalidOperationException(
                "Nested impersonation is not supported. Run the whole filesystem operation inside a single scope.");
        }

        var worker = _workers[(uint)Interlocked.Increment(ref _cursor) % _workers.Length];
        var item = new WorkItem
        {
            Identity = identity,
            Action = () => action(),
            Completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        if (!worker.Queue.TryAdd(item))
        {
            throw new InvalidOperationException("Impersonation worker is not accepting work.");
        }

        return AwaitAsync<T>(item, cancellationToken);
    }

    public async Task RunAsync(LinuxIdentity identity, Action action, CancellationToken cancellationToken = default)
    {
        await RunAsync<object?>(identity, () => { action(); return null; }, cancellationToken).ConfigureAwait(false);
    }

    public void VerifyCredentialSwitch()
    {
        var identity = new LinuxIdentity(_options.SelfTestUid, _options.SelfTestGid, "self-test", [], false);
        var result = RunAsync(identity, () => (Uid: Libc.geteuid(), Gid: Libc.getegid())).AsTask().GetAwaiter().GetResult();

        if (result.Uid != _options.SelfTestUid || result.Gid != _options.SelfTestGid)
        {
            throw new InvalidOperationException(
                $"Impersonation self test failed: expected euid {_options.SelfTestUid}/egid {_options.SelfTestGid}, got euid={result.Uid} egid={result.Gid}.");
        }

        if (Libc.geteuid() != RootId)
        {
            throw new InvalidOperationException("Impersonation self test failed: process did not return to uid 0.");
        }

        if (!ProbeThreadIsolation())
        {
            throw new InvalidOperationException(
                "The current platform applies credential changes process-wide instead of per thread. Serving several " +
                "users from one process would then be unsafe, so the service refuses to start. Credential switching " +
                "requires Linux with glibc >= 2.24 (standard on Debian 12). For local development without root, set " +
                "FileManager:Impersonation:Enabled=false, which also disables real permission enforcement.");
        }

        _logger.LogInformation("Credential switching verified: filesystem operations run as the logged in user.");
    }

    /// <summary>
    /// Verifies that switching credentials on a worker thread is invisible to every other thread.
    /// A background observer must never observe a foreign effective uid.
    /// </summary>
    internal bool ProbeThreadIsolation()
    {
        var foreignObservations = 0;
        var stop = false;

        var observer = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (Libc.geteuid() != RootId)
                {
                    Interlocked.Increment(ref foreignObservations);
                }

                Thread.Sleep(2);
            }
        })
        {
            IsBackground = true,
            Name = "fm-impersonation-isolation-probe",
        };

        observer.Start();

        try
        {
            var identity = new LinuxIdentity(_options.SelfTestUid, _options.SelfTestGid, "self-test", [], false);
            for (var i = 0; i < 3; i++)
            {
                RunAsync(identity, () => Thread.Sleep(15)).GetAwaiter().GetResult();
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            observer.Join(TimeSpan.FromSeconds(2));
        }

        return foreignObservations == 0;
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
        {
            worker.Dispose();
        }
    }

    private static T RunAsRoot<T>(Func<T> action) => action();

    private static async ValueTask<T> AwaitAsync<T>(WorkItem item, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<object?>)state!).TrySetCanceled(), item.Completion);
        var result = await item.Completion.Task.ConfigureAwait(false);
        return (T)result!;
    }

    /// <summary>
    /// Fully synchronous worker loop. It must never await: a continuation could resume on a thread pool
    /// thread and the credentials switched here belong to the calling thread only.
    /// </summary>
    private void RunWorker(Worker worker)
    {
        try
        {
            foreach (var item in worker.Queue.GetConsumingEnumerable())
            {
                object? result = null;
                Exception? failure = null;
                var credentialLoss = false;

                try
                {
                    Enter(item.Identity);
                    result = item.Action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    try
                    {
                        Restore();
                    }
                    catch (Exception restoreFailure)
                    {
                        _logger.LogCritical(restoreFailure, "Failed to restore root credentials on impersonation worker. Shutting the worker down.");
                        credentialLoss = true;
                        failure = restoreFailure;
                    }
                }

                if (credentialLoss)
                {
                    // Never reuse a thread whose credentials are unknown: fail the item and stop consuming.
                    item.Completion.TrySetException(failure ?? new InvalidOperationException("Impersonation state is unknown."));
                    return;
                }

                if (failure is null)
                {
                    item.Completion.TrySetResult(result);
                }
                else
                {
                    item.Completion.TrySetException(failure);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Impersonation worker terminated unexpectedly.");
        }
    }

    /// <summary>
    /// Switches only the effective credentials (setresuid/setresgid with -1 for real and saved), which is
    /// what the kernel uses for filesystem permission checks (fsuid/fsgid plus supplementary groups).
    /// The real/saved uid stays 0 so the worker can return to root afterwards; this is a permission
    /// enforcement mechanism for our own trusted code, not a sandbox against arbitrary code execution.
    /// </summary>
    private static void Enter(LinuxIdentity identity)
    {
        var groups = identity.SupplementaryGroups.Length == 0 ? [identity.Gid] : identity.SupplementaryGroups;
        Check(Libc.setgroups((nuint)groups.Length, groups), "setgroups");
        Check(Libc.setresgid(uint.MaxValue, identity.Gid, uint.MaxValue), "setresgid");
        Check(Libc.setresuid(uint.MaxValue, identity.Uid, uint.MaxValue), "setresuid");
        _insideScope = true;
    }

    private static void Restore()
    {
        _insideScope = false;
        Check(Libc.setresuid(uint.MaxValue, RootId, uint.MaxValue), "setresuid(root)");
        Check(Libc.setresgid(uint.MaxValue, RootId, uint.MaxValue), "setresgid(root)");
        Check(Libc.setgroups(1, [RootId]), "setgroups(root)");
    }

    private static void Check(int rc, string call)
    {
        if (rc != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"{call} failed with errno {errno} ({errno.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }
    }

    private sealed class Worker : IDisposable
    {
        private readonly Thread _thread;

        public Worker(int index, Action<Worker> loop)
        {
            Queue = new BlockingCollection<WorkItem>(new ConcurrentQueue<WorkItem>(), boundedCapacity: 1024);
            _thread = new Thread(() => loop(this))
            {
                IsBackground = true,
                Name = $"fm-impersonation-{index}",
            };
            _thread.Start();
        }

        public BlockingCollection<WorkItem> Queue { get; }

        public void Dispose()
        {
            try
            {
                Queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                // already disposed
            }
        }
    }
}

/// <summary>Used on non-Linux hosts and in tests: executes the action with the process credentials.</summary>
public sealed class InlineImpersonationExecutor : IImpersonationExecutor
{
    public bool IsEnabled => false;

    public ValueTask<T> RunAsync<T>(LinuxIdentity identity, Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new ValueTask<T>(action());
    }

    public Task RunAsync(LinuxIdentity identity, Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }

    public void VerifyCredentialSwitch()
    {
    }
}
