# FileManager web client

Angular 22 standalone SPA for the FileManager API (session cookie auth, filesystem browsing,
host user administration) with Russian/English localization.

## Requirements

- Node 22+ (verified with Node 24.21.0) and npm 11+
- The API running on `http://localhost:8080` for local development

## Commands

```bash
npm ci                        # install dependencies
npm start                     # dev server on http://localhost:4200, /api proxied to :8080
npm run build                 # production build of BOTH locales (default configuration)
npm run build:production      # same as npm run build
npm test                      # (no unit tests are configured for this project)
npm run extract-i18n          # refresh src/locale/messages.ru.xlf from the source templates
npm run i18n:en               # regenerate src/locale/messages.en.xlf from the Russian source
npm run i18n                  # extract + regenerate the English translation
```

The production build writes the localized bundles directly into the API static files:

```
src/FileManager.Api/wwwroot/ru/index.html
src/FileManager.Api/wwwroot/en/index.html
```

`angular.json` sets `outputPath.base` to `../src/FileManager.Api/wwwroot` with an empty `browser`
sub-folder so that localization produces exactly those two directories, and declares
`i18n.sourceLocale = ru` (`baseHref: /ru/`) plus `i18n.locales.en` (`baseHref: /en/`).

## Localization

- Russian (`ru`) is the source language; English (`en`) is translated in
  `src/locale/messages.en.xlf`.
- `scripts/generate-en-translations.mjs` maps every extracted message id to English and preserves
  the `<x id="…"/>` placeholder tags, so `npm run i18n` keeps the two files in sync.
- The language switcher stores the choice in the `fm_lang` cookie (`path=/`) and reloads the same
  route under the other locale prefix, matching `SpaFallbackMiddleware` on the server.
- Static UI text uses `i18n` attributes; strings built in TypeScript use `$localize` with explicit
  `@@id`s.

## Development proxy

`proxy.conf.json` forwards `/api` (and `/health`) to `http://localhost:8080` while keeping the
`Origin`/`Host` headers intact, which satisfies the backend `SameOriginGuardMiddleware`. The dev
server serves the un-prefixed application; the language switcher then only records the cookie
because the localized locale prefixes exist in the server-hosted production build.

## Architecture

- `src/app/core` — DTO interfaces, typed API clients, functional interceptors, guards, path and
  formatting helpers, signal stores (session, capabilities, clipboard, uploads).
- `src/app/layout` — application shell (toolbar, navigation drawer, language and user menus).
- `src/app/features` — `browse` (default screen), `login`, `history`, `profile`, `users`.
- `src/app/shared` — inline SVG icons and Material dialogs.

Every mutating request gets the `X-Requested-With: XMLHttpRequest` header from
`apiRequestInterceptor`; a `401` clears the session and redirects to `/login` unless the request is
an auth probe.

Uploads use a dedicated XHR-backed `HttpClient` because the application-wide fetch backend cannot
report upload progress (`fetch()` has no upload stream); see
`src/app/core/services/upload.store.ts`.
