import { HttpErrorResponse } from '@angular/common/http';

/** Message shown to the user for a failed request, derived from the RFC 9457 problem document. */
export function problemMessage(error: unknown, fallback: string): string {
  return problemFrom(error)?.detail ?? problemFrom(error)?.title ?? statusMessage(error, fallback);
}

/** HTTP status of a failed request, or `null` when the failure was not an HTTP error. */
export function problemStatus(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** Stable machine readable code a problem document may carry (for example `session-superseded`). */
export function problemCode(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse) || !isRecord(error.error)) {
    return null;
  }

  return readString(error.error, 'code');
}

/** `detail` of a problem document, when present. */
export function problemDetail(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse) || !isRecord(error.error)) {
    return null;
  }

  return readString(error.error, 'detail');
}

/** Arbitrary string extension of a problem document (for example `lastSeenUtc`). */
export function problemString(error: unknown, key: string): string | null {
  if (!(error instanceof HttpErrorResponse) || !isRecord(error.error)) {
    return null;
  }

  return readString(error.error, key);
}

interface ProblemParts {
  readonly title: string | null;
  readonly detail: string | null;
}

function problemFrom(error: unknown): ProblemParts | null {
  if (!(error instanceof HttpErrorResponse)) {
    return null;
  }

  const body: unknown = error.error;
  if (!isRecord(body)) {
    return null;
  }

  return {
    title: readString(body, 'title'),
    detail: readString(body, 'detail'),
  };
}

function statusMessage(error: unknown, fallback: string): string {
  const status = problemStatus(error);

  switch (status) {
    case 0:
      return $localize`:@@error.network:Нет связи с сервером. Проверьте подключение.`;
    case 400:
      return $localize`:@@error.badRequest:Некорректный запрос.`;
    case 401:
      return $localize`:@@error.unauthorized:Требуется вход в систему.`;
    case 403:
      return $localize`:@@error.forbidden:Недостаточно прав для выполнения операции.`;
    case 404:
      return $localize`:@@error.notFound:Объект не найден.`;
    case 409:
      return $localize`:@@error.conflict:Конфликт с текущим состоянием.`;
    case 413:
      return $localize`:@@error.tooLarge:Файл слишком большой.`;
    case 429:
      return $localize`:@@error.tooManyRequests:Слишком много запросов. Повторите попытку позже.`;
    default:
      break;
  }

  if (status !== null && status >= 500) {
    return $localize`:@@error.server:Внутренняя ошибка сервера.`;
  }

  if (error instanceof Error && error.message.length > 0) {
    return error.message;
  }

  return fallback;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function readString(source: Record<string, unknown>, key: string): string | null {
  const value = source[key];
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}
