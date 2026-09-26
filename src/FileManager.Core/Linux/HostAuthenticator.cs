using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FileManager.Core.Configuration;
using FileManager.Core.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Linux;

public sealed record AuthenticationOutcome(bool Success, string Provider, string? FailureReason)
{
    public static AuthenticationOutcome Ok(string provider) => new(true, provider, null);

    public static AuthenticationOutcome Fail(string provider, string reason) => new(false, provider, reason);
}

public interface IHostAuthenticator
{
    Task<AuthenticationOutcome> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken);
}

/// <summary>Chooses PAM when usable, otherwise verifies against /etc/shadow with crypt(3).</summary>
public sealed class HostAuthenticator : IHostAuthenticator
{
    private readonly IHostUserDirectory _directory;
    private readonly ILogger<HostAuthenticator> _logger;
    private readonly FileManagerOptions _options;
    private readonly Lazy<IHostAuthenticator?> _pam;

    public HostAuthenticator(
        IHostUserDirectory directory,
        IOptions<FileManagerOptions> options,
        ILogger<HostAuthenticator> logger)
    {
        _directory = directory;
        _options = options.Value;
        _logger = logger;
        _pam = new Lazy<IHostAuthenticator?>(CreatePam);
    }

    public async Task<AuthenticationOutcome> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        if (_options.Auth.AllowDevelopmentBypass)
        {
            var existing = _directory.Find(userName);
            if (existing is null)
            {
                return AuthenticationOutcome.Fail("development", "Пользователь не найден в системе.");
            }

            _logger.LogWarning("Development authentication bypass accepted login for {User}.", userName);
            return AuthenticationOutcome.Ok("development");
        }

        // "shadow" (the default) verifies against /etc/shadow with crypt_r(3) directly: it does not
        // depend on a working PAM stack, which is environment sensitive (containers frequently lack
        // the helper binaries or module configuration, and then every sign-in fails).
        if (_options.Auth.Provider.Equals("shadow", StringComparison.OrdinalIgnoreCase))
        {
            return VerifyWithShadow(userName, password);
        }

        var provider = _pam.Value;
        if (provider is null)
        {
            return VerifyWithShadow(userName, password);
        }

        var outcome = await provider.AuthenticateAsync(userName, password, cancellationToken).ConfigureAwait(false);
        if (outcome.Success || outcome.FailureReason != PamAuthenticator.NotConfiguredReason)
        {
            if (!outcome.Success)
            {
                _logger.LogWarning("PAM authentication for {User} failed: {Reason}", userName, outcome.FailureReason);
            }

            return outcome;
        }

        _logger.LogWarning("PAM is not usable ({Reason}); verifying against /etc/shadow instead.", outcome.FailureReason);
        return VerifyWithShadow(userName, password);
    }

    private IHostAuthenticator? CreatePam()
    {
        if (!PamAuthenticator.IsLibraryAvailable)
        {
            _logger.LogWarning("libpam.so.0 is not available; falling back to /etc/shadow authentication.");
            return null;
        }

        var service = _options.Auth.PamService;
        if (!PamAuthenticator.IsServiceConfigured(service))
        {
            _logger.LogWarning("PAM service {Service} is not configured; falling back to /etc/shadow authentication.", service);
            return null;
        }

        return new PamAuthenticator(service, _logger);
    }

    private AuthenticationOutcome VerifyWithShadow(string userName, string password)
    {
        var entry = _directory.GetShadowEntry(userName);
        return ShadowCryptAuthenticator.Verify(userName, password, entry);
    }
}

/// <summary>Verifies a password against a /etc/shadow hash using crypt_r(3).</summary>
public sealed class ShadowCryptAuthenticator : IHostAuthenticator
{
    public const string ProviderName = "shadow";

    private readonly IHostUserDirectory _directory;

    public ShadowCryptAuthenticator(IHostUserDirectory directory)
    {
        _directory = directory;
    }

    public Task<AuthenticationOutcome> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Verify(userName, password, _directory.GetShadowEntry(userName)));
    }

    /// <summary>
    /// Verifies the password against the shadow entry and applies the ageing rules PAM would apply
    /// for us (account expiration and password expiration).
    /// </summary>
    public static AuthenticationOutcome Verify(string userName, string password, ShadowEntry? entry)
    {
        if (entry is null)
        {
            return AuthenticationOutcome.Fail(ProviderName, "Учётная запись отсутствует в /etc/shadow.");
        }

        if (!AccountFileParser.IsUsablePasswordHash(entry.Hash))
        {
            return AuthenticationOutcome.Fail(ProviderName, "У пользователя не задан пароль.");
        }

        var expiry = CheckAgeing(entry);
        if (expiry is not null)
        {
            return AuthenticationOutcome.Fail(ProviderName, expiry);
        }

        string? computed;
        try
        {
            computed = Hash(password, entry.Hash);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return AuthenticationOutcome.Fail(ProviderName, "Модуль crypt(3) недоступен.");
        }

        if (computed is null)
        {
            // libcrypt could not handle this hash format (unsupported or damaged entry).
            return AuthenticationOutcome.Fail(
                ProviderName,
                $"Не удалось проверить хэш пароля (формат {DescribeFormat(entry.Hash)} не поддерживается libcrypt).");
        }

        var stored = Encoding.UTF8.GetBytes(entry.Hash);
        var actual = Encoding.UTF8.GetBytes(computed);
        var matches = stored.Length == actual.Length && CryptographicOperations.FixedTimeEquals(stored, actual);
        return matches
            ? AuthenticationOutcome.Ok(ProviderName)
            : AuthenticationOutcome.Fail(ProviderName, "Неверное имя пользователя или пароль.");
    }

    /// <summary>Account expiration (field 8) and password expiration (last change + field 5).</summary>
    private static string? CheckAgeing(ShadowEntry entry)
    {
        var today = DateTimeOffset.UtcNow;

        if (ShadowEntry.ToDate(entry.ExpireDays) is { } accountExpires && accountExpires < today)
        {
            return "Срок действия учётной записи истёк.";
        }

        if (entry.MaxDays > 0 && entry.LastChangeDays > 0 &&
            ShadowEntry.ToDate(entry.LastChangeDays + entry.MaxDays) is { } passwordExpires &&
            passwordExpires < today)
        {
            return "Срок действия пароля истёк — смените пароль на хосте.";
        }

        if (entry.InactiveDays > 0 && entry.LastChangeDays > 0 && entry.MaxDays > 0 &&
            ShadowEntry.ToDate(entry.LastChangeDays + entry.MaxDays + entry.InactiveDays) is { } inactiveSince &&
            inactiveSince < today)
        {
            return "Учётная запись деактивирована по неактивности.";
        }

        return null;
    }

    private static string DescribeFormat(string hash) =>
        hash.Length >= 3 ? hash[..3] : "(пустой)";

    private static string? Hash(string password, string storedHash)
    {
        var buffer = Marshal.AllocHGlobal(CryptInterop.CryptDataSize);
        try
        {
            unsafe
            {
                new Span<byte>((void*)buffer, CryptInterop.CryptDataSize).Clear();
            }

            var result = CryptInterop.CryptR(password, storedHash, buffer);
            return result == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(result);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>PAM based authentication (pam_start/pam_authenticate/pam_acct_mgmt) through libpam.</summary>
public sealed class PamAuthenticator : IHostAuthenticator
{
    public const string NotConfiguredReason = "PAM service is not configured.";
    public const string ProviderName = "pam";

    private readonly string _service;
    private readonly ILogger _logger;

    public PamAuthenticator(string service, ILogger logger)
    {
        _service = service;
        _logger = logger;
    }

    public static bool IsLibraryAvailable { get; } = ProbeLibrary();

    public static bool IsServiceConfigured(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine("/etc/pam.d", service));
        }
        catch (IOException)
        {
            return false;
        }
    }

    public Task<AuthenticationOutcome> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Authenticate(userName, password), CancellationToken.None);
    }

    private AuthenticationOutcome Authenticate(string userName, string password)
    {
        var state = new ConversationState(userName, password);
        var stateHandle = GCHandle.Alloc(state);
        PamInterop.ConversationCallback callback = Conversation;
        var callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
        var conversation = new PamInterop.PamConversation
        {
            Conv = callbackPointer,
            AppDataPtr = GCHandle.ToIntPtr(stateHandle),
        };

        var handle = IntPtr.Zero;
        try
        {
            var rc = PamInterop.PamStart(_service, userName, ref conversation, out handle);
            if (rc != PamInterop.PamSuccess)
            {
                _logger.LogWarning("pam_start failed for service {Service} with code {Code}.", _service, rc);
                return AuthenticationOutcome.Fail(ProviderName, NotConfiguredReason);
            }

            rc = PamInterop.PamAuthenticate(handle, 0);
            if (rc != PamInterop.PamSuccess)
            {
                return AuthenticationOutcome.Fail(ProviderName, ErrorText(handle, rc));
            }

            rc = PamInterop.PamAcctMgmt(handle, 0);
            if (rc != PamInterop.PamSuccess)
            {
                return AuthenticationOutcome.Fail(ProviderName, ErrorText(handle, rc));
            }

            return AuthenticationOutcome.Ok(ProviderName);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "libpam is not usable; PAM authentication is unavailable.");
            return AuthenticationOutcome.Fail(ProviderName, NotConfiguredReason);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                PamInterop.PamEnd(handle, 0);
            }

            stateHandle.Free();
            GC.KeepAlive(callback);
        }
    }

    private static string ErrorText(IntPtr handle, int code)
    {
        try
        {
            var pointer = PamInterop.PamStrError(handle, code);
            var text = pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
            return text ?? $"PAM error {code}.";
        }
        catch (DllNotFoundException)
        {
            return $"PAM error {code}.";
        }
    }

    private static int Conversation(int numMsg, IntPtr msgPointer, out IntPtr responsePointer, IntPtr appDataPtr)
    {
        responsePointer = IntPtr.Zero;
        if (numMsg <= 0)
        {
            return PamInterop.PamSuccess;
        }

        var state = (ConversationState?)GCHandle.FromIntPtr(appDataPtr).Target;
        if (state is null)
        {
            return PamInterop.PamConversationError;
        }

        var responses = Marshal.AllocHGlobal(numMsg * IntPtr.Size);
        var size = Marshal.SizeOf<PamInterop.PamResponse>();
        for (var i = 0; i < numMsg; i++)
        {
            var message = Marshal.PtrToStructure<PamInterop.PamMessage>(Marshal.ReadIntPtr(msgPointer, i * IntPtr.Size));
            var answer = message.MsgStyle switch
            {
                PamInterop.PromptEchoOff => state.Password,
                PamInterop.PromptEchoOn => state.UserName,
                _ => null,
            };

            var response = Marshal.AllocHGlobal(size);
            var structure = new PamInterop.PamResponse
            {
                Resp = answer is null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(answer),
                RespRetcode = 0,
            };
            Marshal.StructureToPtr(structure, response, false);
            Marshal.WriteIntPtr(responses, i * IntPtr.Size, response);
        }

        responsePointer = responses;
        return PamInterop.PamSuccess;
    }

    private static bool ProbeLibrary()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryLoad("libpam.so.0", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record ConversationState(string UserName, string Password);
}
