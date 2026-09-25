using FileManager.Core.Data;

namespace FileManager.Api.Auth;

/// <summary>Human readable text for the stable session codes the client also receives.</summary>
public static class SessionMessages
{
    public const string UnauthenticatedCode = "unauthenticated";

    public static string Describe(string? reasonCode) => reasonCode switch
    {
        SessionRevocationReasons.SupersededByNewSignIn =>
            "Сессия заменена новым входом. Одновременная работа из разных браузеров запрещена.",
        SessionRevocationReasons.PageClosed =>
            "Страница приложения была закрыта — сессия завершена. Войдите снова.",
        SessionRevocationReasons.ClosedByAdmin =>
            "Сессия завершена администратором. Войдите снова.",
        SessionRevocationReasons.PasswordChanged => "Пароль был изменён — войдите снова.",
        SessionRevocationReasons.Expired => "Сессия истекла. Войдите снова.",
        SessionRevocationReasons.AccountUnusable => "Учётная запись больше не может входить в систему.",
        SessionRevocationReasons.SignedOut => "Сессия завершена. Войдите снова.",
        SessionRevocationReasons.TabConflict =>
            "Приложение уже открыто в другой вкладке этого браузера. Закройте лишнюю вкладку.",
        _ => "Требуется вход в систему.",
    };

    public static string AlreadySignedIn(DateTime lastSeenUtc) =>
        $"Пользователь уже вошёл в систему в другом браузере (последняя активность {lastSeenUtc:HH:mm:ss} UTC). " +
        "Чтобы войти здесь, выполните выход в том браузере.";
}
