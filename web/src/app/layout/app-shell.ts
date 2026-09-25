import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';

import { browseLink } from '../core/path.utils';
import { APP_LOCALES, AppLocale, LanguageService } from '../core/services/language.service';
import { CurrentUrlStore } from '../core/services/current-url.store';
import { SessionStore } from '../core/services/session.store';
import { Icon } from '../shared/icon/icon';

/** Application chrome: navigation drawer, toolbar, language switcher and the user menu. */
@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatToolbarModule,
    MatSidenavModule,
    MatButtonModule,
    MatMenuModule,
    MatListModule,
    MatTooltipModule,
    Icon,
  ],
  template: `
    <mat-sidenav-container class="shell">
      <mat-sidenav
        class="nav"
        [mode]="compact() ? 'over' : 'side'"
        [opened]="compact() ? navOpened() : true"
        (openedChange)="navOpened.set($event)"
        [attr.aria-label]="navLabel"
      >
        <nav class="nav-links">
          <a mat-list-item routerLink="/browse" routerLinkActive="active" (click)="closeOnCompact()">
            <app-icon name="folder" />
            <span i18n="@@nav.browse">Файлы</span>
          </a>
          <a mat-list-item routerLink="/history" routerLinkActive="active" (click)="closeOnCompact()">
            <app-icon name="history" />
            <span i18n="@@nav.history">История</span>
          </a>
          @if (isAdmin()) {
            <a mat-list-item routerLink="/users" routerLinkActive="active" (click)="closeOnCompact()">
              <app-icon name="users" />
              <span i18n="@@nav.users">Пользователи</span>
            </a>
          }
          <a mat-list-item routerLink="/profile" routerLinkActive="active" (click)="closeOnCompact()">
            <app-icon name="profile" />
            <span i18n="@@nav.profile">Профиль</span>
          </a>
        </nav>
      </mat-sidenav>

      <mat-sidenav-content>
        <mat-toolbar class="toolbar">
          <button
            matIconButton
            type="button"
            (click)="toggleNav()"
            [attr.aria-label]="toggleNavLabel"
            [matTooltip]="toggleNavLabel"
          >
            <app-icon name="menu" />
          </button>

          <span class="app-name" i18n="@@app.name">Файловый менеджер</span>

          <span class="current-path" [title]="currentLocation()">{{ currentLocation() }}</span>

          <span class="spacer"></span>

          <button
            matIconButton
            type="button"
            [matMenuTriggerFor]="languageMenu"
            [attr.aria-label]="languageLabel"
            [matTooltip]="languageLabel"
          >
            <app-icon name="translate" />
          </button>

          <button matButton type="button" [matMenuTriggerFor]="userMenu" class="user-button">
            <app-icon name="profile" />
            <span class="user-name">{{ displayName() }}</span>
          </button>
        </mat-toolbar>

        <main class="content">
          <router-outlet />
        </main>
      </mat-sidenav-content>
    </mat-sidenav-container>

    <mat-menu #languageMenu="matMenu">
      @for (locale of locales; track locale) {
        <button mat-menu-item type="button" (click)="switchLanguage(locale)">
          @if (locale === currentLocale()) {
            <app-icon name="check" />
          } @else {
            <span class="check-placeholder" aria-hidden="true"></span>
          }
          <span>{{ localeName(locale) }}</span>
        </button>
      }
    </mat-menu>

    <mat-menu #userMenu="matMenu">
      <div class="menu-header" role="presentation">
        <strong>{{ displayName() }}</strong>
        @if (profile(); as user) {
          <span class="menu-sub">{{ user.userName }}</span>
        }
      </div>
      <button mat-menu-item type="button" (click)="openProfile()">
        <app-icon name="profile" />
        <span i18n="@@nav.profile">Профиль</span>
      </button>
      <button mat-menu-item type="button" (click)="openHome()">
        <app-icon name="folder" />
        <span i18n="@@nav.home">Домашний каталог</span>
      </button>
      <button mat-menu-item type="button" (click)="logout()">
        <app-icon name="logout" />
        <span i18n="@@nav.logout">Выйти</span>
      </button>
    </mat-menu>
  `,
  styles: `
    .shell {
      height: 100vh;
    }

    .nav {
      width: 15rem;
      border-right: 1px solid var(--mat-sys-outline-variant);
    }

    .nav-links {
      display: flex;
      flex-direction: column;
      padding: 0.5rem;
      gap: 0.15rem;
    }

    .nav-links a {
      display: flex;
      align-items: center;
      gap: 0.6rem;
      padding: 0.6rem 0.75rem;
      border-radius: 0.5rem;
      color: inherit;
      text-decoration: none;
    }

    .nav-links a:hover {
      background: var(--mat-sys-surface-container-high);
    }

    .nav-links a.active {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }

    .toolbar {
      position: sticky;
      top: 0;
      z-index: 5;
      gap: 0.5rem;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface-container);
    }

    .app-name {
      font: var(--mat-sys-title-medium);
      white-space: nowrap;
    }

    .current-path {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      max-width: 40vw;
    }

    .user-name {
      margin-inline-start: 0.35rem;
      max-width: 12rem;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .content {
      min-height: calc(100vh - 4rem);
    }

    .menu-header {
      display: flex;
      flex-direction: column;
      padding: 0.5rem 1rem;
    }

    .menu-sub {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .check-placeholder {
      display: inline-block;
      width: 20px;
    }

    @media (max-width: 1023px) {
      .app-name {
        display: none;
      }
    }
  `,
})
export class AppShell {
  private readonly session = inject(SessionStore);
  private readonly language = inject(LanguageService);
  private readonly urlStore = inject(CurrentUrlStore);
  private readonly router = inject(Router);
  private readonly breakpoints = inject(BreakpointObserver);

  protected readonly profile = this.session.profile;
  protected readonly displayName = this.session.displayName;
  protected readonly isAdmin = this.session.isAdmin;
  protected readonly locales = APP_LOCALES;
  protected readonly currentLocale = this.language.current;

  protected readonly navLabel = $localize`:@@nav.aria:Основная навигация`;
  protected readonly toggleNavLabel = $localize`:@@nav.toggle:Показать или скрыть меню`;
  protected readonly languageLabel = $localize`:@@nav.language:Язык интерфейса`;

  private readonly navOpened = signal(false);
  protected readonly compact = toSignal(
    this.breakpoints.observe('(max-width: 1023px)').pipe(map((result) => result.matches)),
    { initialValue: false },
  );

  protected readonly currentLocation = computed(() => {
    const url = this.urlStore.url();

    if (url.startsWith('/browse')) {
      return this.urlStore.browsePath();
    }

    if (url.startsWith('/users')) {
      return $localize`:@@nav.users:Пользователи`;
    }

    if (url.startsWith('/history')) {
      return $localize`:@@nav.history:История`;
    }

    if (url.startsWith('/profile')) {
      return $localize`:@@nav.profile:Профиль`;
    }

    return '';
  });

  protected toggleNav(): void {
    this.navOpened.update((open) => !open);
  }

  protected closeOnCompact(): void {
    if (this.compact()) {
      this.navOpened.set(false);
    }
  }

  protected localeName(locale: AppLocale): string {
    return locale === 'ru' ? $localize`:@@language.ru:Русский` : $localize`:@@language.en:Английский`;
  }

  protected switchLanguage(locale: AppLocale): void {
    if (locale !== this.currentLocale()) {
      this.language.switchTo(locale);
    }
  }

  protected openProfile(): void {
    void this.router.navigate(['/profile']);
  }

  protected openHome(): void {
    const home = this.profile()?.homeDirectory;
    void this.router.navigate(home !== undefined && home.length > 0 ? browseLink(home) : ['/browse']);
  }

  protected logout(): void {
    this.session.logout().subscribe(() => {
      void this.router.navigate(['/login']);
    });
  }
}
