import { ChangeDetectionStrategy, Component, LOCALE_ID, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';

import { ConflictPolicy, FileEntry } from '../../core/models/api.models';
import { conflictLabel, effectiveAccessLabel, entryTypeLabel } from '../../core/labels';

export interface ConfirmDialogData {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel: string;
  readonly danger?: boolean;
}

/** Yes/no confirmation used for destructive or irreversible actions. */
@Component({
  selector: 'app-confirm-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="dialog-text">{{ data.message }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" (click)="close(false)">
        {{ cancelLabel }}
      </button>
      <button
        matButton="filled"
        type="button"
        [class.danger]="data.danger === true"
        cdkFocusInitial
        (click)="close(true)"
      >
        {{ data.confirmLabel }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .dialog-text {
      margin: 0;
      white-space: pre-line;
    }

    .danger {
      --mat-button-filled-container-color: var(--mat-sys-error);
      --mat-button-filled-label-text-color: var(--mat-sys-on-error);
    }
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);
  protected readonly cancelLabel = $localize`:@@common.cancel:Отмена`;
  private readonly dialogRef = inject<MatDialogRef<ConfirmDialog, boolean>>(MatDialogRef);

  protected close(result: boolean): void {
    this.dialogRef.close(result);
  }
}

export interface PromptDialogData {
  readonly title: string;
  readonly label: string;
  readonly value: string;
  readonly confirmLabel: string;
  /** Rejects `/` because a single entry name cannot contain a path separator. */
  readonly nameOnly?: boolean;
}

/** Single line text prompt used for "new folder" and "rename". */
@Component({
  selector: 'app-prompt-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <mat-form-field appearance="outline" class="full-width">
        <mat-label>{{ data.label }}</mat-label>
        <input
          matInput
          type="text"
          name="value"
          autocomplete="off"
          cdkFocusInitial
          [ngModel]="value()"
          (ngModelChange)="onInput($event)"
          (keydown.enter)="submit()"
        />
        @if (error(); as message) {
          <mat-error>{{ message }}</mat-error>
        }
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" (click)="close(null)">{{ cancelLabel }}</button>
      <button matButton="filled" type="button" [disabled]="error() !== null" (click)="submit()">
        {{ data.confirmLabel }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .full-width {
      width: min(28rem, 70vw);
    }
  `,
})
export class PromptDialog {
  protected readonly data = inject<PromptDialogData>(MAT_DIALOG_DATA);
  protected readonly cancelLabel = $localize`:@@common.cancel:Отмена`;
  protected readonly value = signal(this.data.value);
  protected readonly error = signal<string | null>(null);

  private readonly dialogRef = inject<MatDialogRef<PromptDialog, string | null>>(MatDialogRef);

  constructor() {
    this.validate(this.value());
  }

  protected onInput(next: string): void {
    this.value.set(next);
    this.validate(next);
  }

  protected submit(): void {
    this.validate(this.value());
    if (this.error() !== null) {
      return;
    }

    this.dialogRef.close(this.value().trim());
  }

  protected close(result: string | null): void {
    this.dialogRef.close(result);
  }

  private validate(candidate: string): void {
    const trimmed = candidate.trim();

    if (trimmed.length === 0) {
      this.error.set($localize`:@@prompt.required:Введите имя.`);
      return;
    }

    if (trimmed === '.' || trimmed === '..') {
      this.error.set($localize`:@@prompt.reserved:Недопустимое имя.`);
      return;
    }

    if (this.data.nameOnly !== false && (trimmed.includes('/') || trimmed.includes('\0'))) {
      this.error.set($localize`:@@prompt.noSlash:Имя не может содержать символ «/».`);
      return;
    }

    this.error.set(null);
  }
}

export type DeleteChoice = 'nonRecursive' | 'recursive';

export interface DeleteDialogData {
  readonly names: readonly string[];
  readonly includesDirectories: boolean;
}

/** Delete confirmation that offers "only if empty" for folders. */
@Component({
  selector: 'app-delete-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ title }}</h2>
    <mat-dialog-content>
      <p class="dialog-text">{{ message }}</p>
      @if (data.includesDirectories) {
        <p class="hint">
          {{ directoriesHint }}
        </p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" (click)="close(null)">{{ cancelLabel }}</button>
      @if (data.includesDirectories) {
        <button matButton="outlined" type="button" (click)="close('nonRecursive')">
          {{ emptyOnlyLabel }}
        </button>
      }
      <button matButton="filled" type="button" class="danger" cdkFocusInitial (click)="close('recursive')">
        {{ recursiveLabel }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .dialog-text {
      margin: 0 0 0.5rem;
      white-space: pre-line;
    }

    .hint {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
    }

    .danger {
      --mat-button-filled-container-color: var(--mat-sys-error);
      --mat-button-filled-label-text-color: var(--mat-sys-on-error);
    }
  `,
})
export class DeleteDialog {
  protected readonly data = inject<DeleteDialogData>(MAT_DIALOG_DATA);
  protected readonly cancelLabel = $localize`:@@common.cancel:Отмена`;
  protected readonly emptyOnlyLabel = $localize`:@@delete.emptyOnly:Удалить только пустые`;
  protected readonly recursiveLabel = this.data.includesDirectories
    ? $localize`:@@delete.recursive:Удалить рекурсивно`
    : $localize`:@@common.delete:Удалить`;
  protected readonly directoriesHint = $localize`:@@delete.directoriesHint:Каталоги можно удалить рекурсивно вместе с содержимым и вложенными каталогами.`;
  protected readonly title = $localize`:@@delete.title:Подтвердите удаление`;

  protected readonly message =
    this.data.names.length === 1
      ? $localize`:@@delete.messageOne:Удалить «${this.data.names[0]}:name:»?`
      : $localize`:@@delete.messageMany:Удалить выбранные объекты (${this.data.names.length}:count:)?`;

  private readonly dialogRef = inject<MatDialogRef<DeleteDialog, DeleteChoice | null>>(MatDialogRef);

  protected close(choice: DeleteChoice | null): void {
    this.dialogRef.close(choice);
  }
}

export interface ConflictDialogData {
  readonly conflictingNames: readonly string[];
  readonly totalCount: number;
}

/** Asks how to resolve name collisions during copy/move/upload. */
@Component({
  selector: 'app-conflict-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatRadioModule, FormsModule],
  template: `
    <h2 mat-dialog-title>{{ title }}</h2>
    <mat-dialog-content>
      <p class="dialog-text">
        {{ message }}
      </p>
      @if (data.conflictingNames.length > 0) {
        <ul class="conflicts">
          @for (name of data.conflictingNames.slice(0, 5); track name) {
            <li>{{ name }}</li>
          }
          @if (data.conflictingNames.length > 5) {
            <li>{{ moreLabel }}</li>
          }
        </ul>
      }
      <mat-radio-group [ngModel]="policy()" (ngModelChange)="policy.set($event)" class="policies">
        @for (option of options; track option.value) {
          <mat-radio-button [value]="option.value">{{ option.label }}</mat-radio-button>
        }
      </mat-radio-group>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" (click)="close(null)">{{ cancelLabel }}</button>
      <button matButton="filled" type="button" cdkFocusInitial (click)="close(policy())">
        {{ confirmLabel }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .dialog-text {
      margin: 0 0 0.5rem;
    }

    .conflicts {
      margin: 0 0 0.75rem;
      padding-left: 1.25rem;
      max-height: 8rem;
      overflow: auto;
    }

    .policies {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }
  `,
})
export class ConflictDialog {
  protected readonly data = inject<ConflictDialogData>(MAT_DIALOG_DATA);
  protected readonly policy = signal<ConflictPolicy>('overwrite');
  protected readonly cancelLabel = $localize`:@@common.cancel:Отмена`;
  protected readonly confirmLabel = $localize`:@@common.apply:Применить`;
  protected readonly title = $localize`:@@conflict.title:Обнаружены совпадения имён`;
  protected readonly moreLabel = $localize`:@@conflict.more:…и другие`;
  protected readonly message = $localize`:@@conflict.message:В целевой папке уже есть объекты с такими именами. Выберите действие:`;

  protected readonly options: readonly { value: ConflictPolicy; label: string }[] = [
    { value: 'overwrite', label: conflictLabel('overwrite') },
    { value: 'skip', label: conflictLabel('skip') },
    { value: 'rename', label: conflictLabel('rename') },
    { value: 'fail', label: conflictLabel('fail') },
  ];

  private readonly dialogRef = inject<MatDialogRef<ConflictDialog, ConflictPolicy | null>>(MatDialogRef);

  protected close(result: ConflictPolicy | null): void {
    this.dialogRef.close(result);
  }
}

export interface PropertiesDialogData {
  readonly entry: FileEntry;
}

/** Read-only details of a single entry, including symlink target and effective access. */
@Component({
  selector: 'app-properties-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ title }}</h2>
    <mat-dialog-content>
      <dl class="properties">
        <dt>{{ nameLabel }}</dt>
        <dd class="breakable">{{ data.entry.name }}</dd>

        <dt>{{ pathLabel }}</dt>
        <dd class="breakable">{{ data.entry.fullPath }}</dd>

        <dt>{{ typeLabel }}</dt>
        <dd>{{ type }}</dd>

        <dt>{{ sizeLabel }}</dt>
        <dd>{{ size }}</dd>

        <dt>{{ modifiedLabel }}</dt>
        <dd>{{ modified }}</dd>

        <dt>{{ modeLabel }}</dt>
        <dd class="mono">{{ data.entry.mode }}</dd>

        <dt>{{ ownerLabel }}</dt>
        <dd>{{ data.entry.owner }} / {{ data.entry.group }}</dd>

        <dt>{{ accessLabel }}</dt>
        <dd>
          <span class="mono">{{ data.entry.effectiveAccess }}</span>
          — {{ accessDescription }}
        </dd>

        @if (data.entry.linkTarget; as target) {
          <dt>{{ targetLabel }}</dt>
          <dd class="breakable mono">{{ target }}</dd>
        }
      </dl>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton="filled" type="button" cdkFocusInitial (click)="close()">{{ closeLabel }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .properties {
      display: grid;
      grid-template-columns: minmax(8rem, max-content) 1fr;
      gap: 0.35rem 1rem;
      margin: 0;
      min-width: min(34rem, 70vw);
    }

    dt {
      color: var(--mat-sys-on-surface-variant);
    }

    dd {
      margin: 0;
    }

    .mono {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    }

    .breakable {
      overflow-wrap: anywhere;
    }
  `,
})
export class PropertiesDialog {
  protected readonly data = inject<PropertiesDialogData>(MAT_DIALOG_DATA);
  private readonly locale = inject(LOCALE_ID);
  protected readonly title = $localize`:@@properties.title:Свойства`;
  protected readonly nameLabel = $localize`:@@properties.name:Имя`;
  protected readonly pathLabel = $localize`:@@properties.path:Полный путь`;
  protected readonly typeLabel = $localize`:@@properties.type:Тип`;
  protected readonly sizeLabel = $localize`:@@properties.size:Размер`;
  protected readonly modifiedLabel = $localize`:@@properties.modified:Изменён`;
  protected readonly modeLabel = $localize`:@@properties.mode:Права (mode)`;
  protected readonly ownerLabel = $localize`:@@properties.owner:Владелец / группа`;
  protected readonly accessLabel = $localize`:@@properties.access:Доступ`;
  protected readonly targetLabel = $localize`:@@properties.linkTarget:Ссылка на`;
  protected readonly closeLabel = $localize`:@@common.close:Закрыть`;
  protected readonly type = entryTypeLabel(this.data.entry.type);
  protected readonly accessDescription = effectiveAccessLabel(this.data.entry.effectiveAccess);
  protected readonly size = formatSize(this.data.entry.size, this.locale);
  protected readonly modified = formatModified(this.data.entry.lastWriteTimeUtc, this.locale);

  private readonly dialogRef = inject<MatDialogRef<PropertiesDialog, void>>(MatDialogRef);

  protected close(): void {
    this.dialogRef.close();
  }
}

function formatSize(bytes: number, locale: string): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return '—';
  }

  return new Intl.NumberFormat(locale, {
    style: 'unit',
    unit: bytes >= 1024 ? 'kilobyte' : 'byte',
    unitDisplay: 'short',
    maximumFractionDigits: 1,
  }).format(bytes >= 1024 ? bytes / 1024 : bytes);
}

function formatModified(isoTimestamp: string, locale: string): string {
  const date = new Date(isoTimestamp);
  return Number.isNaN(date.getTime())
    ? '—'
    : new Intl.DateTimeFormat(locale, { dateStyle: 'short', timeStyle: 'medium' }).format(date);
}
