import { Injectable } from '@angular/core';

const TAB_KEY = 'fm.tabId';

/**
 * Identity of this browser tab.
 *
 * A backend session belongs to exactly one tab: the first tab that uses it claims the lease and
 * every other tab of the same browser gets `409 tab-conflict`. The identifier lives in
 * `sessionStorage`, which is per tab, so each tab presents its own value in `X-Tab-Id`.
 */
@Injectable({ providedIn: 'root' })
export class TabIdentity {
  readonly tabId: string = readTabId();

  /**
   * Tells the server that this page is going away. `navigator.sendBeacon` survives the unload and
   * cannot set custom headers, hence the identifier in the query string. A reload cancels the close
   * server side within the grace window, so F5 does not sign the user out.
   */
  reportPageClosed(): void {
    const url = `/api/auth/page-closed?tabId=${encodeURIComponent(this.tabId)}`;

    try {
      if (globalThis.navigator?.sendBeacon?.(url) === true) {
        return;
      }
    } catch {
      // fall through to fetch
    }

    try {
      void globalThis.fetch?.(url, { method: 'POST', keepalive: true, credentials: 'same-origin' });
    } catch {
      // the browser is going away anyway
    }
  }
}

function readTabId(): string {
  try {
    const existing = globalThis.sessionStorage?.getItem(TAB_KEY);
    if (existing !== null && existing !== undefined && existing.length > 0) {
      return existing;
    }

    const created = createId();
    globalThis.sessionStorage?.setItem(TAB_KEY, created);
    return created;
  } catch {
    return createId();
  }
}

function createId(): string {
  const cryptoApi = globalThis.crypto;
  if (cryptoApi?.randomUUID !== undefined) {
    return cryptoApi.randomUUID();
  }

  return `tab-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}
