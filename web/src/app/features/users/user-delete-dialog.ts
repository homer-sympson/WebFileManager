import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

export interface UserDeleteDialogData {
  readonly userName: string;
  readonly homeDirectory: string;
  readonly aclPaths: readonly string[];
}

export interface UserDeleteDialogResult {
  readonly removeHome: boolean;
  readonly revokePaths: readonly string[];
}

/** Deletion options for a host account: home directory and ACL grants are removed explicitly. */
@Component({
  selector: 'app-user-delete-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, FormsModule],
  template: `
    <h2 mat-dialog-title>{{ title }}</h2>
    <mat-dialog-content>
      <p class="text">
        {{ message }}
      </p>

      <mat-checkbox [checked]="removeHome()" (change)="removeHome.set($event.checked)">
        {{ removeHomeLabel }}
      </mat-checkbox>

      @if (data.aclPaths.length > 0) {
        <fieldset class="paths">
          <legend i18n="@@users.deleteRevokeLegend">Отозвать ACL на путях</legend>
          @for (path of data.aclPaths; track path) {
            <mat-checkbox [checked]="isRevoked(path)" (change)="toggleRevoke(path, $event.checked)">
              {{ path }}
            </mat-checkbox>
          }
        </fieldset>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" (click)="close(null)" i18n="@@common.cancel">Отмена</button>
      <button matButton="filled" type="button" class="danger" cdkFocusInitial (click)="confirm()">
        <span i18n="@@users.deleteConfirm">Удалить пользователя</span>
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .text {
      margin: 0 0 0.75rem;
      white-space: pre-line;
    }

    .paths {
      margin-top: 0.75rem;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 0.5rem;
      padding: 0.5rem 0.75rem;
      display: flex;
      flex-direction: column;
      max-height: 12rem;
      overflow: auto;
    }

    legend {
      padding: 0 0.25rem;
      color: var(--mat-sys-on-surface-variant);
    }

    .danger {
      --mat-button-filled-container-color: var(--mat-sys-error);
      --mat-button-filled-label-text-color: var(--mat-sys-on-error);
    }
  `,
})
export class UserDeleteDialog {
  protected readonly data = inject<UserDeleteDialogData>(MAT_DIALOG_DATA);
  protected readonly removeHome = signal(false);
  protected readonly revoked = signal<ReadonlySet<string>>(new Set<string>());
  protected readonly title = $localize`:@@users.deleteTitle:Удаление учётной записи`;
  protected readonly removeHomeLabel = $localize`:@@users.deleteRemoveHome:Удалить домашний каталог ${this.data.homeDirectory}:path:`;
  protected readonly message = $localize`:@@users.deleteMessage:Учётная запись «${this.data.userName}:name:» будет удалена без возможности восстановления.`;

  private readonly dialogRef = inject<MatDialogRef<UserDeleteDialog, UserDeleteDialogResult | null>>(MatDialogRef);

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

  protected confirm(): void {
    this.close({ removeHome: this.removeHome(), revokePaths: [...this.revoked()] });
  }

  protected close(result: UserDeleteDialogResult | null): void {
    this.dialogRef.close(result);
  }
}
