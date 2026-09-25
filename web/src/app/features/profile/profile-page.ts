import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';

import { browseLink } from '../../core/path.utils';
import { CapabilitiesStore } from '../../core/services/capabilities.store';
import { SessionStore } from '../../core/services/session.store';
import { Icon } from '../../shared/icon/icon';

/** Current account plus the server capability matrix. */
@Component({
  selector: 'app-profile-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, Icon],
  template: `
    <div class="page">
      <header class="page-header">
        <h1 i18n="@@profile.title">Профиль и возможности системы</h1>
      </header>

      @if (profile(); as user) {
        <section class="card" aria-labelledby="profile-user">
          <h2 id="profile-user" i18n="@@profile.account">Учётная запись</h2>
          <dl class="grid">
            <dt i18n="@@profile.userName">Имя пользователя</dt>
            <dd>{{ user.userName }}</dd>
            <dt i18n="@@profile.fullName">Полное имя</dt>
            <dd>{{ user.fullName || '—' }}</dd>
            <dt i18n="@@profile.uidGid">UID / GID</dt>
            <dd>{{ user.uid }} / {{ user.gid }}</dd>
            <dt i18n="@@profile.home">Домашний каталог</dt>
            <dd>
              <button type="button" class="link" (click)="openPath(user.homeDirectory)">
                {{ user.homeDirectory }}
              </button>
            </dd>
            <dt i18n="@@profile.groups">Группы</dt>
            <dd>{{ user.groups.length > 0 ? user.groups.join(', ') : '—' }}</dd>
            <dt i18n="@@profile.admin">Администратор</dt>
            <dd>
              @if (user.isAdmin) {
                <span class="yes" i18n="@@common.yes">Да</span>
              } @else {
                <span i18n="@@common.no">Нет</span>
              }
            </dd>
            <dt i18n="@@profile.sudo">Правило sudo</dt>
            <dd>
              @if (user.hasSudoRule) {
                <span class="yes" i18n="@@common.yes">Да</span>
              } @else {
                <span i18n="@@common.no">Нет</span>
              }
            </dd>
          </dl>
        </section>
      }

      <section class="card" aria-labelledby="profile-system">
        <h2 id="profile-system" i18n="@@profile.system">Возможности сервера</h2>

        @if (capabilities(); as caps) {
          @if (!caps.aclAvailable) {
            <div class="banner warning" role="status">
              <app-icon name="warning" />
              <span i18n="@@profile.aclUnavailable">
                POSIX ACL недоступны: управление правами на каталоги через ACL отключено, списки доступа
                недоступны.
              </span>
            </div>
          }

          @if (!caps.databaseReady) {
            <div class="banner error" role="alert">
              <app-icon name="warning" />
              <span i18n="@@profile.databaseUnavailable">
                База данных недоступна: история посещений и сессии могут работать некорректно.
              </span>
            </div>
          }

          <dl class="grid">
            <dt i18n="@@profile.os">Операционная система</dt>
            <dd>{{ caps.os }}</dd>
            <dt i18n="@@profile.privileged">Запуск с правами root</dt>
            <dd>{{ flag(caps.isPrivileged) }}</dd>
            <dt i18n="@@profile.impersonation">Подмена пользователя</dt>
            <dd>{{ flag(caps.impersonationEnabled) }}</dd>
            <dt i18n="@@profile.pam">PAM</dt>
            <dd>{{ flag(caps.pamAvailable) }}</dd>
            <dt i18n="@@profile.shadow">/etc/shadow</dt>
            <dd>{{ flag(caps.shadowAvailable) }}</dd>
            <dt i18n="@@profile.acl">POSIX ACL</dt>
            <dd>{{ flag(caps.aclAvailable) }}</dd>
            <dt i18n="@@profile.database">База данных</dt>
            <dd>{{ flag(caps.databaseReady) }}</dd>
            <dt i18n="@@profile.roots">Разрешённые корни</dt>
            <dd class="roots">
              @for (root of caps.browseRoots; track root) {
                <button type="button" class="link" (click)="openPath(root)">{{ root }}</button>
              }
              @if (caps.browseRoots.length === 0) {
                <span i18n="@@profile.noRoots">не заданы</span>
              }
            </dd>
          </dl>
        } @else {
          <mat-progress-bar mode="indeterminate" />
        }
      </section>
    </div>
  `,
  styles: `
    .card {
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 0.75rem;
      padding: 1rem;
      margin-bottom: 1rem;
      background: var(--mat-sys-surface-container-low);
    }

    .card h2 {
      font: var(--mat-sys-title-medium);
      margin: 0 0 0.75rem;
    }

    .grid {
      display: grid;
      grid-template-columns: minmax(10rem, max-content) minmax(0, 1fr);
      gap: 0.35rem 1rem;
      margin: 0;
    }

    dt {
      color: var(--mat-sys-on-surface-variant);
    }

    dd {
      margin: 0;
      overflow-wrap: anywhere;
    }

    .link {
      padding: 0;
      border: none;
      background: transparent;
      color: var(--mat-sys-primary);
      font: inherit;
      cursor: pointer;
    }

    .link:hover {
      text-decoration: underline;
    }

    .roots {
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem;
    }

    .yes {
      color: var(--mat-sys-primary);
      font-weight: 600;
    }

    .banner {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.6rem 0.75rem;
      border-radius: 0.6rem;
      margin-bottom: 0.75rem;
    }

    .banner.warning {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }

    .banner.error {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
  `,
})
export class ProfilePage {
  private readonly session = inject(SessionStore);
  private readonly capabilitiesStore = inject(CapabilitiesStore);
  private readonly router = inject(Router);

  protected readonly profile = this.session.profile;
  protected readonly capabilities = this.capabilitiesStore.capabilities;

  constructor() {
    this.session.ensureLoaded().subscribe();
    this.capabilitiesStore.ensureLoaded().subscribe();
  }

  protected flag(value: boolean): string {
    return value ? $localize`:@@common.yes:Да` : $localize`:@@common.no:Нет`;
  }

  protected openPath(path: string): void {
    void this.router.navigate(browseLink(path));
  }
}
