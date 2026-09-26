import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { Router } from '@angular/router';

import { ActiveSession, SessionsApi } from '../../core/api/sessions.api';
import { UsersApi } from '../../core/api/users.api';
import { problemMessage } from '../../core/http/problem';
import { HostUser, HostUserList } from '../../core/models/api.models';
import { CapabilitiesStore } from '../../core/services/capabilities.store';
import { NotificationService } from '../../core/services/notification.service';
import { SessionStore } from '../../core/services/session.store';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/dialogs/dialogs';
import { Icon } from '../../shared/icon/icon';
import {
  UserDeleteDialog,
  UserDeleteDialogData,
  UserDeleteDialogResult,
} from './user-delete-dialog';

/** Admin-only host account list. */
@Component({
  selector: 'app-user-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatTableModule,
    Icon,
  ],
  template: `
    <div class="page">
      <header class="page-header">
        <h1 i18n="@@users.title">Пользователи системы</h1>
        <span class="spacer"></span>
        <button matButton="filled" type="button" (click)="create()">
          <app-icon name="users" />
          <span i18n="@@users.create">Создать пользователя</span>
        </button>
      </header>

      <div class="filters">
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
          <mat-label i18n="@@users.search">Поиск</mat-label>
          <input
            matInput
            type="search"
            name="search"
            autocomplete="off"
            [ngModel]="search()"
            (ngModelChange)="search.set($event)"
          />
        </mat-form-field>

        <mat-checkbox [checked]="includeSystem()" (change)="setIncludeSystem($event.checked)">
          <span i18n="@@users.includeSystem">Показывать системных пользователей</span>
        </mat-checkbox>

        <button matButton type="button" (click)="load()" i18n="@@common.refresh">Обновить</button>
      </div>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }

      @if (error(); as message) {
        <div class="banner error" role="alert">
          <app-icon name="warning" />
          <span>{{ message }}</span>
        </div>
      }

      @if (!aclAvailable() && list() !== null) {
        <div class="banner warning" role="status">
          <app-icon name="warning" />
          <span i18n="@@users.aclUnavailable">
            POSIX ACL недоступны на сервере: управление правами на каталоги отключено.
          </span>
        </div>
      }

      <table mat-table [dataSource]="filtered()" class="users-table">
        <ng-container matColumnDef="userName">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colName">Пользователь</th>
          <td mat-cell *matCellDef="let user">
            <button type="button" class="link" (click)="edit(user)">{{ user.userName }}</button>
          </td>
        </ng-container>

        <ng-container matColumnDef="uid">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colUid">UID / GID</th>
          <td mat-cell *matCellDef="let user">{{ user.uid }} / {{ user.gid }}</td>
        </ng-container>

        <ng-container matColumnDef="fullName">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colFullName">Полное имя</th>
          <td mat-cell *matCellDef="let user">{{ user.fullName || '—' }}</td>
        </ng-container>

        <ng-container matColumnDef="homeDirectory">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colHome">Домашний каталог</th>
          <td mat-cell *matCellDef="let user" class="wrap">{{ user.homeDirectory || '—' }}</td>
        </ng-container>

        <ng-container matColumnDef="shell">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colShell">Оболочка</th>
          <td mat-cell *matCellDef="let user" class="wrap">{{ user.shell || '—' }}</td>
        </ng-container>

        <ng-container matColumnDef="groups">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colGroups">Группы</th>
          <td mat-cell *matCellDef="let user" class="wrap">{{ user.groups.join(', ') || '—' }}</td>
        </ng-container>

        <ng-container matColumnDef="flags">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colFlags">Признаки</th>
          <td mat-cell *matCellDef="let user">
            @if (user.isAdmin) {
              <span class="chip" i18n="@@users.flagAdmin">админ</span>
            }
            @if (user.hasPassword) {
              <span class="chip" i18n="@@users.flagPassword">пароль</span>
            } @else {
              <span class="chip muted" i18n="@@users.flagNoPassword">без пароля</span>
            }
          </td>
        </ng-container>

        <ng-container matColumnDef="session">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colSession">Сессия</th>
          <td mat-cell *matCellDef="let user" class="wrap">
            @if (sessionFor(user.userName); as session) {
              <span class="chip" i18n="@@users.sessionActive">активна</span>
              <span class="session-note">
                {{ formatMoment(session.lastSeenUtc) }}
                @if (session.remoteIp) {
                  · {{ session.remoteIp }}
                }
              </span>
            } @else {
              <span class="muted" i18n="@@users.sessionNone">нет</span>
            }
          </td>
        </ng-container>

        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef i18n="@@users.colActions">Действия</th>
          <td mat-cell *matCellDef="let user">
            <button matButton type="button" (click)="edit(user)" i18n="@@common.edit">Изменить</button>
            @if (sessionFor(user.userName); as session) {
              <button matButton type="button" (click)="closeSession(user, session)" i18n="@@users.closeSession">
                Завершить сессию
              </button>
            }
            <button matButton type="button" (click)="remove(user)" i18n="@@common.delete">Удалить</button>
          </td>
        </ng-container>

        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>

      @if (!loading() && filtered().length === 0) {
        <div class="state-message">
          <app-icon name="users" [size]="40" />
          <p i18n="@@users.empty">Пользователи не найдены.</p>
        </div>
      }
    </div>
  `,
  styles: `
    .filters {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 1rem;
      margin-bottom: 1rem;
    }

    .search {
      min-width: 16rem;
    }

    .users-table {
      width: 100%;
    }

    .wrap {
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

    .chip {
      display: inline-block;
      margin-inline-end: 0.35rem;
      padding: 0.05rem 0.45rem;
      border-radius: 0.75rem;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      font: var(--mat-sys-label-small);
    }

    .chip.muted {
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }

    .session-note {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .muted {
      color: var(--mat-sys-on-surface-variant);
    }

    .banner {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.6rem 0.75rem;
      border-radius: 0.6rem;
      margin-bottom: 0.75rem;
    }

    .banner.error {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }

    .banner.warning {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }
  `,
})
export class UserListPage {
  private readonly usersApi = inject(UsersApi);
  private readonly sessionsApi = inject(SessionsApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotificationService);
  private readonly capabilities = inject(CapabilitiesStore);
  private readonly session = inject(SessionStore);

  protected readonly columns = [
    'userName',
    'uid',
    'fullName',
    'homeDirectory',
    'shell',
    'groups',
    'flags',
    'session',
    'actions',
  ] as const;

  protected readonly list = signal<HostUserList | null>(null);
  protected readonly sessions = signal<readonly ActiveSession[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly includeSystem = signal(false);
  // A signal: `filtered` is a computed and would never see a plain field change.
  protected readonly search = signal('');

  protected readonly aclAvailable = computed(() => this.list()?.aclAvailable ?? false);

  /** Active session of a host account, or `null` when that user is not signed in. */
  protected sessionFor(userName: string): ActiveSession | null {
    return this.sessions().find((entry) => entry.userName === userName) ?? null;
  }

  protected formatMoment(value: string): string {
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) {
      return value;
    }

    return new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'short' }).format(parsed);
  }

  protected readonly filtered = computed<readonly HostUser[]>(() => {
    const users = this.list()?.users ?? [];
    const term = this.search().trim().toLowerCase();

    if (term.length === 0) {
      return users;
    }

    return users.filter((user) =>
      [user.userName, user.fullName, user.homeDirectory, user.shell, user.groups.join(' ')]
        .join(' ')
        .toLowerCase()
        .includes(term),
    );
  });

  constructor() {
    this.capabilities.ensureLoaded().subscribe();
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.usersApi.list(this.includeSystem()).subscribe({
      next: (list) => {
        this.list.set(list);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.error.set(problemMessage(error, $localize`:@@users.loadFailed:Не удалось загрузить список пользователей.`));
      },
    });

    // Session oversight is a best effort: the user list must render even when it fails.
    this.sessionsApi.list().subscribe({
      next: (list) => this.sessions.set(list.sessions),
      error: () => this.sessions.set([]),
    });
  }

  /**
   * Frees an account whose browser is gone: without it a session that was never closed keeps
   * blocking every other sign-in until its idle timeout.
   */
  protected closeSession(user: HostUser, session: ActiveSession): void {
    this.dialog
      .open(ConfirmDialog, {
        data: {
          title: $localize`:@@users.closeSessionTitle:Завершить сессию`,
          message: session.isCurrentSession
            ? $localize`:@@users.closeSessionSelf:Это ваша текущая сессия. Она будет завершена, и потребуется войти заново. Продолжить?`
            : $localize`:@@users.closeSessionConfirm:Завершить активную сессию пользователя ${user.userName}:name:? Ему потребуется войти заново.`,
          confirmLabel: $localize`:@@users.closeSessionConfirmLabel:Завершить сессию`,
          danger: true,
        } satisfies ConfirmDialogData,
        width: 'auto',
      })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed !== true) {
          return;
        }

        this.sessionsApi.close(user.userName).subscribe({
          next: () => {
            this.notify.success($localize`:@@users.sessionClosed:Сессия завершена.`);

            if (session.isCurrentSession) {
              // Our own session is gone: leave the signed-in shell immediately.
              this.session.clear();
              void this.router.navigate(['/login']);
              return;
            }

            this.load();
          },
          error: (error: unknown) =>
            this.notify.error(problemMessage(error, $localize`:@@users.sessionCloseFailed:Не удалось завершить сессию.`)),
        });
      });
  }

  protected setIncludeSystem(value: boolean): void {
    this.includeSystem.set(value);
    this.load();
  }

  protected create(): void {
    void this.router.navigate(['/users', 'new']);
  }

  protected edit(user: HostUser): void {
    void this.router.navigate(['/users', user.userName]);
  }

  protected remove(user: HostUser): void {
    this.usersApi.details(user.userName, this.inspectPaths()).subscribe({
      next: (details) => {
        const data: UserDeleteDialogData = {
          userName: user.userName,
          homeDirectory: details.homeDirectory,
          aclPaths: details.access.filter((entry) => entry.error === null).map((entry) => entry.path),
        };

        this.dialog
          .open(UserDeleteDialog, { data, width: 'auto' })
          .afterClosed()
          .subscribe((result: UserDeleteDialogResult | null) => {
            if (result === null) {
              return;
            }

            this.usersApi.delete(user.userName, result.removeHome, result.revokePaths).subscribe({
              next: () => {
                this.notify.success($localize`:@@users.deleted:Пользователь удалён.`);
                this.load();
              },
              error: (error: unknown) =>
                this.notify.error(problemMessage(error, $localize`:@@users.deleteFailed:Не удалось удалить пользователя.`)),
            });
          });
      },
      error: (error: unknown) =>
        this.notify.error(problemMessage(error, $localize`:@@users.detailsFailed:Не удалось получить данные пользователя.`)),
    });
  }

  /** ACL inspection is only meaningful inside the folders the server is allowed to browse. */
  private inspectPaths(): readonly string[] {
    const roots = this.capabilities.browseRoots();
    return roots.length > 0 ? roots : ['/'];
  }
}
