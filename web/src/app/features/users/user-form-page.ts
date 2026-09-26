import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule, NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, of } from 'rxjs';

import { UsersApi } from '../../core/api/users.api';
import { problemMessage } from '../../core/http/problem';
import { pathAccessLabel } from '../../core/labels';
import {
  AclEntry,
  CreateUserRequest,
  HostUserDetails,
  PathAccess,
  PathGrantRequest,
  UpdateUserRequest,
} from '../../core/models/api.models';
import { normalizePath } from '../../core/path.utils';
import { CapabilitiesStore } from '../../core/services/capabilities.store';
import { NotificationService } from '../../core/services/notification.service';
import { Icon } from '../../shared/icon/icon';
import {
  UserDeleteDialog,
  UserDeleteDialogData,
  UserDeleteDialogResult,
} from './user-delete-dialog';

interface GrantRow {
  readonly path: string;
  readonly access: PathAccess;
  readonly isDefault: boolean;
}

const SHELL_SUGGESTIONS: readonly string[] = ['/bin/bash', '/bin/sh', '/bin/zsh', '/usr/bin/fish', '/usr/sbin/nologin'];
const MIN_PASSWORD_LENGTH = 8;

/** Create/edit form for a host account, including ACL grants and the sudo rule. */
@Component({
  selector: 'app-user-form-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    ReactiveFormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    Icon,
  ],
  template: `
    <div class="page">
      <header class="page-header">
        <button matButton type="button" (click)="back()" i18n="@@common.back">Назад</button>
        <h1>
          @if (isNew()) {
            <span i18n="@@users.newTitle">Новый пользователь</span>
          } @else {
            <span i18n="@@users.editTitle">Пользователь {{ name() }}</span>
          }
        </h1>
        <span class="spacer"></span>
        @if (!isNew()) {
          <button matButton type="button" (click)="remove()" i18n="@@common.delete">Удалить</button>
        }
      </header>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }

      @if (error(); as message) {
        <div class="banner error" role="alert">
          <app-icon name="warning" />
          <span>{{ message }}</span>
        </div>
      }

      <form [formGroup]="form" (ngSubmit)="submit()" class="form">
        <section class="card">
          <h2 i18n="@@users.sectionAccount">Учётная запись</h2>

          <div class="rows">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label i18n="@@users.fieldUserName">Имя пользователя</mat-label>
              <input matInput type="text" formControlName="userName" autocomplete="off" spellcheck="false" />
              @if (isNew() && form.controls.userName.hasError('required')) {
                <mat-error i18n="@@users.userNameRequired">Укажите имя пользователя.</mat-error>
              }
              @if (isNew() && form.controls.userName.hasError('pattern')) {
                <mat-error i18n="@@users.userNamePattern">
                  До 32 символов: строчные латинские буквы, цифры, «_» и «-», первый символ — буква или «_».
                </mat-error>
              }
            </mat-form-field>

            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label i18n="@@users.fieldFullName">Полное имя</mat-label>
              <input matInput type="text" formControlName="fullName" autocomplete="off" />
            </mat-form-field>

            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label i18n="@@users.fieldShell">Оболочка</mat-label>
              <input matInput type="text" formControlName="shell" list="shell-suggestions" spellcheck="false" />
              <datalist id="shell-suggestions">
                @for (shell of shells; track shell) {
                  <option [value]="shell"></option>
                }
              </datalist>
            </mat-form-field>

            @if (isNew()) {
              <div class="inline">
                <mat-checkbox formControlName="createHome" i18n="@@users.fieldCreateHome">
                  Создать домашний каталог
                </mat-checkbox>
              </div>

              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label i18n="@@users.fieldHome">Домашний каталог</mat-label>
                <input matInput type="text" formControlName="homeDirectory" spellcheck="false" />
              </mat-form-field>
            } @else {
              <div class="readonly-row">
                <span class="label" i18n="@@users.fieldHome">Домашний каталог</span>
                <span class="mono">{{ details()?.homeDirectory || '—' }}</span>
              </div>
              <div class="readonly-row">
                <span class="label" i18n="@@users.fieldUid">UID / GID</span>
                <span class="mono">{{ details()?.uid }} / {{ details()?.gid }}</span>
              </div>
            }
          </div>
        </section>

        <section class="card">
          <h2 i18n="@@users.sectionPassword">Пароль</h2>
          <div class="rows">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label i18n="@@users.fieldPassword">Пароль</mat-label>
              <input
                #passwordInput
                matInput
                type="password"
                formControlName="password"
                autocomplete="new-password"
                (blur)="adoptPasswordValues()"
                (change)="adoptPasswordValues()"
              />
              @if (isNew()) {
                <mat-hint i18n="@@users.passwordHint">Минимум {{ minPasswordLength }} символов.</mat-hint>
              } @else {
                <mat-hint i18n="@@users.passwordKeepHint">Оставьте пустым, чтобы не менять пароль.</mat-hint>
              }
            </mat-form-field>

            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label i18n="@@users.fieldPasswordConfirm">Повторите пароль</mat-label>
              <input
                #confirmPasswordInput
                matInput
                type="password"
                formControlName="confirmPassword"
                autocomplete="new-password"
                (blur)="adoptPasswordValues()"
                (change)="adoptPasswordValues()"
              />
            </mat-form-field>
          </div>

          @if (passwordError(); as message) {
            <p class="error-text">{{ message }}</p>
          }
        </section>

        <section class="card">
          <h2 i18n="@@users.sectionGroups">Группы</h2>

          <div class="rows">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label i18n="@@users.fieldGroups">Группы пользователя</mat-label>
              <mat-select
                multiple
                [value]="selectedGroups()"
                (selectionChange)="onGroupsChange($event.value)"
                i18n-placeholder="@@users.groupsPlaceholder"
                placeholder="Выберите группы"
              >
                @for (group of selectableGroups(); track group) {
                  <mat-option [value]="group">{{ group }}</mat-option>
                }
              </mat-select>
            </mat-form-field>

            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label i18n="@@users.fieldNewGroup">Добавить группу</mat-label>
              <input matInput type="text" name="newGroup" [(ngModel)]="newGroup" [ngModelOptions]="{ standalone: true }" />
            </mat-form-field>

            <button matButton="outlined" type="button" (click)="addGroup()" i18n="@@users.addGroup">
              Добавить группу
            </button>
          </div>
        </section>

        <section class="card">
          <h2 i18n="@@users.sectionSudo">Права sudo</h2>

          <mat-checkbox formControlName="grantSudo" i18n="@@users.fieldGrantSudo">
            Разрешить sudo
          </mat-checkbox>

          <mat-checkbox formControlName="sudoNopasswd" i18n="@@users.fieldSudoNopasswd">
            sudo без запроса пароля (NOPASSWD)
          </mat-checkbox>

          @if (!isNew() && details()?.hasSudoRule) {
            <mat-checkbox formControlName="revokeSudo" i18n="@@users.fieldRevokeSudo">
              Удалить существующее правило sudo
            </mat-checkbox>

            @if (details()?.sudoRule; as rule) {
              <pre class="rule" aria-label="sudoers rule">{{ rule }}</pre>
            }
            @if (details()?.sudoersPath; as path) {
              <p class="muted mono">{{ path }}</p>
            }
          }
        </section>

        <section class="card">
          <h2 i18n="@@users.sectionPaths">Права на каталоги (ACL)</h2>

          @if (!aclAvailable() && !isNew()) {
            <div class="banner warning" role="status">
              <app-icon name="warning" />
              <span i18n="@@users.aclUnavailable">
                POSIX ACL недоступны на сервере: управление правами на каталоги отключено.
              </span>
            </div>
          }

          @for (grant of grants(); track $index) {
            <div class="grant-row">
              <mat-form-field appearance="outline" subscriptSizing="dynamic" class="grow">
                <mat-label i18n="@@users.fieldPath">Путь</mat-label>
                <input
                  matInput
                  type="text"
                  spellcheck="false"
                  [value]="grant.path"
                  (input)="updateGrant($index, { path: asValue($event) })"
                />
              </mat-form-field>

              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label i18n="@@users.fieldAccess">Доступ</mat-label>
                <mat-select
                  [value]="grant.access"
                  (selectionChange)="updateGrant($index, { access: asAccess($event.value) })"
                >
                  @for (option of accessOptions; track option) {
                    <mat-option [value]="option">{{ accessLabel(option) }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>

              <mat-checkbox
                [checked]="grant.isDefault"
                (change)="updateGrant($index, { isDefault: $event.checked })"
                i18n="@@users.fieldDefaultAcl"
              >
                По умолчанию
              </mat-checkbox>

              <button matButton type="button" (click)="removeGrant($index)" i18n="@@common.remove">Убрать</button>
            </div>
          }

          <button matButton="outlined" type="button" (click)="addGrant()" i18n="@@users.addPathGrant">
            Добавить путь
          </button>
        </section>

        @if (!isNew()) {
          <section class="card">
            <h2 i18n="@@users.sectionAcl">Существующие ACL</h2>

            <div class="rows">
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label i18n="@@users.inspectPath">Проверить путь</mat-label>
                <input matInput type="text" name="inspectPath" [(ngModel)]="inspectInput" [ngModelOptions]="{ standalone: true }" spellcheck="false" />
              </mat-form-field>
              <button matButton="outlined" type="button" (click)="inspect()" i18n="@@users.inspect">
                Проверить ACL
              </button>
            </div>

            @if (details()?.access?.length) {
              <ul class="acl-list">
                @for (info of details()?.access ?? []; track info.path) {
                  <li class="acl-item">
                    <div class="acl-head">
                      <span class="mono">{{ info.path }}</span>
                      <mat-checkbox
                        [checked]="isRevoked(info.path)"
                        (change)="toggleRevoke(info.path, $event.checked)"
                        i18n="@@users.revokeAcl"
                      >
                        Отозвать
                      </mat-checkbox>
                    </div>

                    @if (info.error; as problem) {
                      <p class="error-text">{{ problem }}</p>
                    } @else if (info.entries.length === 0) {
                      <p class="muted" i18n="@@users.noAclEntries">ACL-записи для этого пользователя отсутствуют.</p>
                    } @else {
                      <ul class="acl-entries">
                        @for (entry of info.entries; track entryKey(entry)) {
                          <li class="mono">
                            {{ entry.userName }}: {{ entry.permissions }}
                            @if (entry.isDefault) {
                              <span class="chip" i18n="@@users.defaultAcl">по умолчанию</span>
                            }
                          </li>
                        }
                      </ul>
                    }
                  </li>
                }
              </ul>
            } @else {
              <p class="muted" i18n="@@users.noAcl">Нет данных ACL. Укажите путь и нажмите «Проверить ACL».</p>
            }
          </section>
        }

        <footer class="actions">
          <button matButton="filled" type="submit" [disabled]="saving() || passwordError() !== null">
            <app-icon name="check" />
            <span i18n="@@common.save">Сохранить</span>
          </button>
          <button matButton type="button" (click)="back()" i18n="@@common.cancel">Отмена</button>
        </footer>
      </form>
    </div>
  `,
  styles: `
    .form {
      display: block;
    }

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

    .rows {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.75rem 1rem;
    }

    .rows > mat-form-field {
      min-width: 16rem;
      flex: 1 1 16rem;
    }

    .readonly-row {
      display: flex;
      gap: 0.5rem;
      min-width: 16rem;
    }

    .label {
      color: var(--mat-sys-on-surface-variant);
    }

    .grant-row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.5rem 1rem;
      margin-bottom: 0.5rem;
    }

    .grow {
      flex: 1 1 18rem;
    }

    .mono {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      overflow-wrap: anywhere;
    }

    .muted {
      color: var(--mat-sys-on-surface-variant);
    }

    .rule {
      margin: 0.5rem 0 0;
      padding: 0.5rem 0.75rem;
      border-radius: 0.5rem;
      background: var(--mat-sys-surface-container-highest);
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 0.85rem;
      overflow: auto;
      white-space: pre-wrap;
    }

    .acl-list,
    .acl-entries {
      list-style: none;
      margin: 0;
      padding: 0;
    }

    .acl-item {
      padding: 0.5rem 0;
      border-top: 1px solid var(--mat-sys-outline-variant);
    }

    .acl-head {
      display: flex;
      align-items: center;
      gap: 1rem;
      flex-wrap: wrap;
    }

    .acl-entries {
      margin-top: 0.25rem;
      padding-left: 1rem;
    }

    .chip {
      margin-inline-start: 0.4rem;
      padding: 0.05rem 0.4rem;
      border-radius: 0.75rem;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      font: var(--mat-sys-label-small);
    }

    .actions {
      display: flex;
      gap: 0.5rem;
      align-items: center;
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
export class UserFormPage {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly usersApi = inject(UsersApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotificationService);
  private readonly capabilities = inject(CapabilitiesStore);

  protected readonly minPasswordLength = MIN_PASSWORD_LENGTH;
  protected readonly shells = SHELL_SUGGESTIONS;
  protected readonly accessOptions: readonly PathAccess[] = ['read', 'write', 'readWrite'];

  private readonly passwordInput = viewChild<ElementRef<HTMLInputElement>>('passwordInput');
  private readonly confirmPasswordInput = viewChild<ElementRef<HTMLInputElement>>('confirmPasswordInput');

  protected readonly isNew = signal(true);
  protected readonly name = signal<string>('');
  protected readonly details = signal<HostUserDetails | null>(null);
  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly groups = signal<readonly string[]>([]);
  protected readonly selectedGroups = signal<readonly string[]>([]);
  protected readonly grants = signal<readonly GrantRow[]>([]);
  protected readonly revoked = signal<ReadonlySet<string>>(new Set<string>());
  protected newGroup = '';
  protected inspectInput = '';

  protected readonly form = this.fb.group({
    // Keep in sync with UserNameValidator on the server: 32 characters max, no dots, no trailing '$'.
    userName: ['', [Validators.required, Validators.pattern(/^[a-z_][a-z0-9_-]{0,31}$/)]],
    password: [''],
    confirmPassword: [''],
    fullName: [''],
    shell: [''],
    createHome: [true],
    homeDirectory: [''],
    grantSudo: [false],
    sudoNopasswd: [false],
    revokeSudo: [false],
  });

  protected readonly aclAvailable = computed(() => this.capabilities.aclAvailable());

  protected readonly selectableGroups = computed(() => {
    const all = new Set<string>([...this.groups(), ...this.selectedGroups()]);
    return [...all].sort((left, right) => left.localeCompare(right));
  });

  /**
   * Password state is deliberately a method rather than a `computed`: reactive form values are plain
   * properties, not signals, so a computed would be evaluated once and never recomputed — which left
   * the save button disabled forever after typing a password.
   */
  protected passwordError(): string | null {
    const password = this.form.controls.password.value;
    const confirmation = this.form.controls.confirmPassword.value;

    if (this.isNew() && password.length === 0) {
      return $localize`:@@users.passwordRequired:Укажите пароль.`;
    }

    if (password.length > 0 && password.length < MIN_PASSWORD_LENGTH) {
      return $localize`:@@users.passwordTooShort:Пароль короче ${MIN_PASSWORD_LENGTH}:min: символов.`;
    }

    if (password !== confirmation) {
      return $localize`:@@users.passwordMismatch:Пароли не совпадают.`;
    }

    return null;
  }

  /**
   * Password managers can write into the inputs without dispatching events, so Angular never sees the
   * value. Adopt whatever is in the DOM before validating or saving.
   */
  protected adoptPasswordValues(): void {
    this.adoptPassword('password', this.passwordInput()?.nativeElement.value);
    this.adoptPassword('confirmPassword', this.confirmPasswordInput()?.nativeElement.value);
  }

  private adoptPassword(name: 'password' | 'confirmPassword', value: string | undefined): void {
    if (value === undefined || value === '') {
      return;
    }

    const control = this.form.controls[name];
    if (control.value === value) {
      return;
    }

    control.setValue(value, { emitEvent: false });
    control.markAsDirty();
  }

  constructor() {
    this.capabilities.ensureLoaded().subscribe();

    this.usersApi
      .groups()
      .pipe(catchError(() => of<readonly string[]>([])))
      .subscribe((groups) => this.groups.set(groups));

    const name = this.route.snapshot.paramMap.get('name');
    if (name === null || name.length === 0) {
      this.isNew.set(true);
      this.form.controls.userName.enable();
      return;
    }

    this.isNew.set(false);
    this.name.set(name);
    this.form.controls.userName.setValue(name);
    this.form.controls.userName.disable();
    this.load(name);
  }

  // ------------------------------------------------------------------ loading

  private load(name: string): void {
    this.loading.set(true);
    this.error.set(null);

    this.usersApi.details(name, this.capabilities.browseRoots()).subscribe({
      next: (details) => {
        this.details.set(details);
        this.selectedGroups.set(details.groups);
        this.form.patchValue({
          fullName: details.fullName,
          shell: details.shell,
          createHome: false,
          homeDirectory: details.homeDirectory,
          grantSudo: details.hasSudoRule,
          sudoNopasswd: false,
          revokeSudo: false,
        });
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.error.set(
          problemMessage(error, $localize`:@@users.detailsFailed:Не удалось получить данные пользователя.`),
        );
      },
    });
  }

  // ------------------------------------------------------------------ editing

  protected asValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected asAccess(value: unknown): PathAccess {
    return value === 'read' || value === 'write' || value === 'readWrite' ? value : 'readWrite';
  }

  protected accessLabel(access: PathAccess): string {
    return pathAccessLabel(access);
  }

  protected entryKey(entry: AclEntry): string {
    return `${entry.userName}|${entry.permissions}|${entry.isDefault ? 'd' : 'n'}`;
  }

  protected onGroupsChange(value: unknown): void {
    this.selectedGroups.set(Array.isArray(value) ? value.map((item) => String(item)) : []);
  }

  protected addGroup(): void {
    const candidate = this.newGroup.trim();
    if (candidate.length === 0) {
      return;
    }

    this.selectedGroups.update((current) =>
      current.includes(candidate) ? current : [...current, candidate],
    );
    this.newGroup = '';
  }

  protected addGrant(): void {
    this.grants.update((current) => [...current, { path: '', access: 'readWrite', isDefault: false }]);
  }

  protected updateGrant(index: number, patch: Partial<GrantRow>): void {
    this.grants.update((current) =>
      current.map((row, position) => (position === index ? { ...row, ...patch } : row)),
    );
  }

  protected removeGrant(index: number): void {
    this.grants.update((current) => current.filter((_, position) => position !== index));
  }

  protected isRevoked(path: string): boolean {
    return this.revoked().has(path);
  }

  protected toggleRevoke(path: string, checked: boolean): void {
    this.revoked.update((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(path);
      } else {
        next.delete(path);
      }

      return next;
    });
  }

  protected inspect(): void {
    const candidate = this.inspectInput.trim();
    if (candidate.length === 0) {
      return;
    }

    const path = normalizePath(candidate);
    const paths = new Set<string>([...this.capabilities.browseRoots(), path]);
    this.inspectInput = '';
    this.reloadWithPaths([...paths]);
  }

  private reloadWithPaths(paths: readonly string[]): void {
    this.loading.set(true);
    this.usersApi.details(this.name(), paths).subscribe({
      next: (details) => {
        this.details.set(details);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.notify.error(
          problemMessage(error, $localize`:@@users.detailsFailed:Не удалось получить данные пользователя.`),
        );
      },
    });
  }

  // ------------------------------------------------------------------ saving

  protected submit(): void {
    // Pick up values a password manager may have written straight into the inputs.
    this.adoptPasswordValues();

    if (this.passwordError() !== null) {
      return;
    }

    this.saving.set(true);
    const values = this.form.getRawValue();
    const pathGrants = this.validGrants();

    if (this.isNew()) {
      const request: CreateUserRequest = {
        userName: values.userName.trim(),
        password: values.password,
        fullName: emptyToNull(values.fullName),
        shell: emptyToNull(values.shell),
        createHome: values.createHome,
        homeDirectory: emptyToNull(values.homeDirectory),
        groups: this.selectedGroups(),
        grantSudo: values.grantSudo,
        sudoNopasswd: values.sudoNopasswd,
        pathGrants,
      };

      this.usersApi.create(request).subscribe({
        next: (details) => {
          this.saving.set(false);
          this.notify.success($localize`:@@users.created:Пользователь создан.`);
          void this.router.navigate(['/users', details.name]);
        },
        error: (error: unknown) => {
          this.saving.set(false);
          this.notify.error(problemMessage(error, $localize`:@@users.createFailed:Не удалось создать пользователя.`));
        },
      });

      return;
    }

    const request: UpdateUserRequest = {
      fullName: emptyToNull(values.fullName),
      shell: emptyToNull(values.shell),
      password: values.password.length > 0 ? values.password : null,
      groups: this.selectedGroups(),
      grantSudo: values.revokeSudo ? false : values.grantSudo,
      sudoNopasswd: values.sudoNopasswd,
      pathGrants,
      revokeAclPaths: [...this.revoked()],
    };

    this.usersApi.update(this.name(), request).subscribe({
      next: (details) => {
        this.saving.set(false);
        this.details.set(details);
        this.grants.set([]);
        this.revoked.set(new Set<string>());
        this.form.patchValue({ password: '', confirmPassword: '' });
        this.notify.success($localize`:@@users.updated:Изменения сохранены.`);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.notify.error(problemMessage(error, $localize`:@@users.updateFailed:Не удалось сохранить изменения.`));
      },
    });
  }

  private validGrants(): readonly PathGrantRequest[] {
    return this.grants()
      .filter((row) => row.path.trim().length > 0)
      .map((row) => ({
        path: normalizePath(row.path),
        access: row.access,
        default: row.isDefault,
      }));
  }

  protected remove(): void {
    const details = this.details();
    if (details === null) {
      return;
    }

    const data: UserDeleteDialogData = {
      userName: details.name,
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

        this.usersApi.delete(details.name, result.removeHome, result.revokePaths).subscribe({
          next: () => {
            this.notify.success($localize`:@@users.deleted:Пользователь удалён.`);
            void this.router.navigate(['/users']);
          },
          error: (error: unknown) =>
            this.notify.error(problemMessage(error, $localize`:@@users.deleteFailed:Не удалось удалить пользователя.`)),
        });
      });
  }

  protected back(): void {
    void this.router.navigate(['/users']);
  }
}

function emptyToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
