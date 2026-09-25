import { problemCode, problemDetail, problemString } from './http/problem';
import type { SessionEndedNotice } from './services/session.store';

/**
 * The backend ends a session for several reasons; it always sends a stable code so the text can be
 * localized on the client, with the server side `detail` as a fallback.
 */
const LOCALIZED_MESSAGES: Record<string, () => string> = {
  'session-superseded': () =>
    $localize`:@@session.superseded:Выполнен вход с другого устройства или браузера. Одновременная работа из разных браузеров запрещена — эта сессия закрыта.`,
  'session-password-changed': () => $localize`:@@session.passwordChanged:Пароль был изменён. Войдите снова.`,
  'session-expired': () => $localize`:@@session.expired:Сессия истекла. Войдите снова.`,
  'session-account-unusable': () => $localize`:@@session.accountUnusable:Учётная запись больше не может входить в систему.`,
  'signed-out': () => $localize`:@@session.signedOut:Сессия завершена. Войдите снова.`,
};

/** Codes that only mean "you were never signed in"; they must not be presented as an ended session. */
const SILENT_CODES = new Set(['unauthenticated']);

/** Builds the notice for a 401 response; `null` when the response carries nothing to explain. */
export function sessionEndedNotice(error: unknown): SessionEndedNotice | null {
  const code = problemCode(error);
  const detail = problemDetail(error);

  if (code !== null && SILENT_CODES.has(code)) {
    return null;
  }

  if (code === null && detail === null) {
    return null;
  }

  return { code, detail };
}

/** Localized explanation of a session termination, falling back to the server message. */
export function sessionEndedMessage(notice: SessionEndedNotice): string {
  const factory = notice.code === null ? undefined : LOCALIZED_MESSAGES[notice.code];
  if (factory !== undefined) {
    return factory();
  }

  return notice.detail ?? $localize`:@@session.generic:Сессия завершена. Войдите снова.`;
}

/**
 * Message for a refused sign-in. The server refuses a second browser with `already-signed-in` and
 * reports when the other session was last active, which is localized here.
 */
export function loginRefusedMessage(error: unknown, fallback: string): string {
  if (problemCode(error) === 'already-signed-in') {
    const lastSeen = problemString(error, 'lastSeenUtc');
    const moment = lastSeen === null ? null : formatMoment(lastSeen);

    if (moment !== null) {
      return $localize`:@@login.alreadySignedInAt:Учётная запись уже используется в другом браузере (последняя активность в ${moment}:moment:). Выполните выход там, чтобы войти здесь.`;
    }

    return $localize`:@@login.alreadySignedIn:Учётная запись уже используется в другом браузере. Выполните выход там, чтобы войти здесь.`;
  }

  return problemDetail(error) ?? fallback;
}

function formatMoment(value: string): string | null {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return null;
  }

  return new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'medium' }).format(parsed);
}
