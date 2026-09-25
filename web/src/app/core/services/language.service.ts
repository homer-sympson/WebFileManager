import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { Router } from '@angular/router';

export type AppLocale = 'ru' | 'en';

export const APP_LOCALES: readonly AppLocale[] = ['ru', 'en'];

const LANGUAGE_COOKIE = 'fm_lang';
const COOKIE_MAX_AGE_SECONDS = 31_536_000;

/** Locale of the currently served bundle, taken from the localized `index.html`. */
function detectLocale(document: Document): AppLocale {
  const fromDocument = (document.documentElement.lang || '').slice(0, 2).toLowerCase();
  if (isAppLocale(fromDocument)) {
    return fromDocument;
  }

  const fromCookie = readCookie(document, LANGUAGE_COOKIE);
  return isAppLocale(fromCookie) ? fromCookie : 'ru';
}

/** Locale prefix of the deployed bundle (`/ru/` or `/en/`), or `null` for an un-prefixed dev build. */
function detectBaseLocale(document: Document): AppLocale | null {
  const basePath = new URL(document.baseURI).pathname;
  const match = /^\/(ru|en)\/?$/.exec(basePath);
  const candidate = match?.[1]?.toLowerCase();

  return candidate !== undefined && isAppLocale(candidate) ? candidate : null;
}

function isAppLocale(value: string | null | undefined): value is AppLocale {
  return value === 'ru' || value === 'en';
}

function readCookie(document: Document, name: string): string | null {
  for (const part of document.cookie.split(';')) {
    const [key, ...rest] = part.split('=');
    if (key?.trim() === name) {
      return decodeURIComponent(rest.join('=').trim());
    }
  }

  return null;
}

/** Switches the UI language by remembering the choice in `fm_lang` and reloading the bundle. */
@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);

  private readonly currentSignal = signal<AppLocale>(detectLocale(this.document));
  private readonly baseLocale = detectBaseLocale(this.document);

  readonly current = this.currentSignal.asReadonly();
  readonly locales = APP_LOCALES;
  readonly localePrefixSupported = this.baseLocale !== null;

  switchTo(locale: AppLocale): void {
    this.writeLanguageCookie(locale);
    this.currentSignal.set(locale);

    const view = this.document.defaultView;
    if (view === null) {
      return;
    }

    const target = this.buildTarget(locale);
    view.location.assign(target);
  }

  private buildTarget(locale: AppLocale): string {
    const currentUrl = this.router.url;

    // Localized production bundles are served below the locale prefix; a plain dev build is not.
    if (this.baseLocale !== null) {
      const path = currentUrl.startsWith('/') ? currentUrl : `/${currentUrl}`;
      return `/${locale}${path}`;
    }

    return currentUrl;
  }

  private writeLanguageCookie(locale: AppLocale): void {
    this.document.cookie = `${LANGUAGE_COOKIE}=${locale}; path=/; max-age=${COOKIE_MAX_AGE_SECONDS}; samesite=lax`;
  }
}
