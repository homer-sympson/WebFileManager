import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, of } from 'rxjs';

import { AuthApi } from '../../core/api/auth.api';
import { loginRefusedMessage, sessionEndedMessage } from '../../core/session-messages';
import { NotificationService } from '../../core/services/notification.service';
import { SessionStore } from '../../core/services/session.store';
import { Icon } from '../../shared/icon/icon';

/** Sign-in screen; remembers the URL the user originally asked for. */
@Component({
  selector: 'app-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    Icon,
  ],
  template: `
    <div class="login">
      <mat-card class="card" appearance="outlined">
        <mat-card-header>
          <mat-card-title i18n="@@login.title">Вход в файловый менеджер</mat-card-title>
        </mat-card-header>

        <mat-card-content>
          @if (submitting()) {
            <mat-progress-bar mode="indeterminate" />
          }

          @if (endedMessage(); as notice) {
            <div class="banner notice" role="status">
              <app-icon name="warning" />
              <span>{{ notice }}</span>
            </div>
          }

          @if (errorMessage(); as message) {
            <div class="banner error" role="alert">
              <app-icon name="warning" />
              <span>{{ message }}</span>
            </div>
          }

          <form [formGroup]="form" (ngSubmit)="submit()" class="form">
            <mat-form-field appearance="outline">
              <mat-label i18n="@@login.userName">Имя пользователя</mat-label>
              <input
                matInput
                type="text"
                formControlName="userName"
                autocomplete="username"
                spellcheck="false"
                cdkFocusInitial
                [attr.list]="exposeUserList() ? 'login-users' : null"
              />
              @if (form.controls.userName.hasError('required')) {
                <mat-error i18n="@@login.userNameRequired">Укажите имя пользователя.</mat-error>
              }
            </mat-form-field>

            <mat-form-field appearance="outline">
              <mat-label i18n="@@login.password">Пароль</mat-label>
              <input
                matInput
                [type]="hidePassword() ? 'password' : 'text'"
                formControlName="password"
                autocomplete="current-password"
              />
              @if (form.controls.password.hasError('required')) {
                <mat-error i18n="@@login.passwordRequired">Укажите пароль.</mat-error>
              }
            </mat-form-field>

            <label class="toggle">
              <input type="checkbox" [checked]="hidePassword()" (change)="hidePassword.set(!hidePassword())" />
              <span i18n="@@login.hidePassword">Скрыть пароль</span>
            </label>

            <button matButton="filled" type="submit" [disabled]="submitting()" class="submit">
              <app-icon name="lock" />
              <span i18n="@@login.submit">Войти</span>
            </button>
          </form>

          <datalist id="login-users">
            @for (user of users(); track user) {
              <option [value]="user"></option>
            }
          </datalist>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: `
    .login {
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100vh;
      padding: 1rem;
      background: var(--mat-sys-surface-container);
    }

    .card {
      width: min(26rem, 100%);
      padding: 0.5rem;
    }

    .form {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      margin-top: 1rem;
    }

    .submit {
      align-self: flex-start;
      margin-top: 0.5rem;
    }

    .toggle {
      display: inline-flex;
      align-items: center;
      gap: 0.4rem;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .banner {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.6rem 0.75rem;
      border-radius: 0.6rem;
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
      margin-bottom: 0.5rem;
    }

    .banner.notice {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
  `,
})
export class LoginPage {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly session = inject(SessionStore);
  private readonly authApi = inject(AuthApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly notify = inject(NotificationService);

  protected readonly form = this.fb.group({
    userName: ['', [Validators.required]],
    password: ['', [Validators.required]],
  });

  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly users = signal<readonly string[]>([]);
  protected readonly exposeUserList = signal(false);
  protected readonly hidePassword = signal(true);

  /** Explanation shown when the server closed a previous session (single client policy, expiry...). */
  protected readonly endedMessage = computed(() => {
    const notice = this.session.ended();
    return notice === null ? null : sessionEndedMessage(notice);
  });

  constructor() {
    this.authApi
      .loginOptions()
      .pipe(catchError(() => of(null)))
      .subscribe((options) => {
        if (options !== null) {
          this.exposeUserList.set(options.exposeUserList);
          this.users.set(options.users);
        }
      });

    this.session.ensureLoaded().subscribe((profile) => {
      if (profile !== null) {
        this.redirectAfterLogin();
      }
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { userName, password } = this.form.getRawValue();
    this.submitting.set(true);
    this.errorMessage.set(null);
    this.session.dismissEnded();

    this.session.login(userName, password).subscribe({
      next: (profile) => {
        this.submitting.set(false);
        this.notify.success($localize`:@@login.welcome:Здравствуйте, ${profile.fullName || profile.userName}:name:!`);
        this.redirectAfterLogin();
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.errorMessage.set(
          loginRefusedMessage(error, $localize`:@@login.failed:Не удалось войти. Проверьте имя пользователя и пароль.`),
        );
      },
    });
  }

  private redirectAfterLogin(): void {
    const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
    const target =
      returnUrl !== null && returnUrl.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/browse';

    void this.router.navigateByUrl(target);
  }
}
