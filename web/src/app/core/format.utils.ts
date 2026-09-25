/** Locale aware formatting helpers built on `Intl`, so no locale data registration is required. */

const BYTE_UNITS = [
  'byte',
  'kilobyte',
  'megabyte',
  'gigabyte',
  'terabyte',
  'petabyte',
] as const satisfies readonly Intl.NumberFormatOptions['unit'][];

const FALLBACK_UNIT_LABELS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'] as const;

/** Human readable file size, e.g. `1,5 МБ`. */
export function formatFileSize(bytes: number, locale: string): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return '—';
  }

  let value = bytes;
  let unitIndex = 0;

  while (value >= 1024 && unitIndex < BYTE_UNITS.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }

  const fractionDigits = unitIndex === 0 ? 0 : value < 10 ? 1 : 0;

  try {
    return new Intl.NumberFormat(locale, {
      style: 'unit',
      unit: BYTE_UNITS[unitIndex],
      unitDisplay: 'short',
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits,
    }).format(value);
  } catch {
    return `${value.toFixed(fractionDigits)} ${FALLBACK_UNIT_LABELS[unitIndex]}`;
  }
}

/** Localized date and time for an ISO-8601 timestamp. */
export function formatDateTime(isoTimestamp: string, locale: string): string {
  const date = new Date(isoTimestamp);
  if (Number.isNaN(date.getTime())) {
    return '—';
  }

  try {
    return new Intl.DateTimeFormat(locale, { dateStyle: 'short', timeStyle: 'medium' }).format(date);
  } catch {
    return date.toISOString();
  }
}

/** Localized relative "time ago" label, falling back to the absolute date for old entries. */
export function formatRelativeTime(isoTimestamp: string, locale: string): string {
  const date = new Date(isoTimestamp);
  if (Number.isNaN(date.getTime())) {
    return '—';
  }

  const diffSeconds = Math.round((date.getTime() - Date.now()) / 1000);
  const absolute = Math.abs(diffSeconds);

  try {
    const formatter = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
    if (absolute < 60) {
      return formatter.format(diffSeconds, 'second');
    }
    if (absolute < 3600) {
      return formatter.format(Math.round(diffSeconds / 60), 'minute');
    }
    if (absolute < 86_400) {
      return formatter.format(Math.round(diffSeconds / 3600), 'hour');
    }
    if (absolute < 2_592_000) {
      return formatter.format(Math.round(diffSeconds / 86_400), 'day');
    }
  } catch {
    return formatDateTime(isoTimestamp, locale);
  }

  return formatDateTime(isoTimestamp, locale);
}
