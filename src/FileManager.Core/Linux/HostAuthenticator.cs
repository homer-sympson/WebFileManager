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

        var provider = _options.Auth.Provider.Equals("pam", StringComparison.OrdinalIgnoreCase) ? _pam.Value : null;
        if (provider is not null)
        {
            var outcome = await provider.AuthenticateAsync(userName, password, cancellationToken).ConfigureAwait(false);
            if (outcome.Success || outcome.FailureReason != PamAuthenticator.NotConfiguredReason)
            {
                return outcome;
            }
        }

        return await VerifyWithShadowAsync(userName, password, cancellationToken).ConfigureAwait(false);
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

    private Task<AuthenticationOutcome> VerifyWithShadowAsync(string userName, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = _directory.GetPasswordHash(userName);
        var outcome = ShadowCryptAuthenticator.Verify(userName, password, hash);
        return Task.FromResult(outcome);
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
        return Task.FromResult(Verify(userName, password, _directory.GetPasswordHash(userName)));
    }

    public static AuthenticationOutcome Verify(string userName, string password, string storedHash)
    {
        if (!AccountFileParser.IsUsablePasswordHash(storedHash))
        {
            return AuthenticationOutcome.Fail(ProviderName, "У пользователя не задан пароль.");
        }

        string? computed;
        try
        {
            computed = Hash(password, storedHash);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return AuthenticationOutcome.Fail(ProviderName, "Модуль crypt(3) недоступен.");
        }

        if (computed is null)
        {
            return AuthenticationOutcome.Fail(ProviderName, "Не удалось вычислить хэш пароля.");
        }

        var stored = Encoding.UTF8.GetBytes(storedHash);
        var actual = Encoding.UTF8.GetBytes(computed);
        var matches = stored.Length == actual.Length && CryptographicOperations.FixedTimeEquals(stored, actual);
        return matches
            ? AuthenticationOutcome.Ok(ProviderName)
            : AuthenticationOutcome.Fail(ProviderName, "Неверное имя пользователя или пароль.");
    }

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
