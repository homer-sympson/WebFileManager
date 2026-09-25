import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { SessionStore } from './core/services/session.store';
import { TabIdentity } from './core/services/tab-identity';

/**
 * Root component; the visible chrome lives in `AppShell`. It also owns two cross-cutting concerns:
 * reporting the page close to the server, and blocking a tab that does not own the session.
 */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  template: `
    <router-outlet />

    @if (session.tabConflict()) {
      <div class="conflict" role="alertdialog" aria-live="assertive">
        <div class="panel">
          <h1 i18n="@@tabConflict.title">Открыто в другой вкладке</h1>
          <p i18n="@@tabConflict.body">
            Работа с файловым менеджером уже идёт в другой вкладке этого браузера. Одна сессия — одна
            вкладка: закройте эту вкладку и продолжайте в той, где вы вошли.
          </p>
        </div>
      </div>
    }
  `,
  styles: `
    .conflict {
      position: fixed;
      inset: 0;
      z-index: 1000;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
      background: var(--mat-sys-scrim, rgb(0 0 0 / 40%));
      color: var(--mat-sys-on-surface);
    }

    .panel {
      max-width: 32rem;
      padding: 1.5rem 1.75rem;
      border-radius: 0.75rem;
      background: var(--mat-sys-surface-container-high);
      box-shadow: var(--mat-sys-level3);
    }

    h1 {
      margin: 0 0 0.5rem;
      font: var(--mat-sys-headline-small);
    }

    p {
      margin: 0;
      font: var(--mat-sys-body-medium);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class App {
  protected readonly session = inject(SessionStore);
  private readonly tab = inject(TabIdentity);

  constructor() {
    // Logout is forced when the page goes away: the browser tells the server, which ends the session
    // unless the same tab comes back within the grace window (a reload).
    globalThis.addEventListener?.('pagehide', () => this.tab.reportPageClosed());
  }
}
