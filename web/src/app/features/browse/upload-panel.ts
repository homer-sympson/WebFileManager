import { ChangeDetectionStrategy, Component, LOCALE_ID, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { formatFileSize } from '../../core/format.utils';
import { UploadStore } from '../../core/services/upload.store';
import { Icon } from '../../shared/icon/icon';

/** Progress panel for the upload queue; reads the `UploadStore` provided by the browse page. */
@Component({
  selector: 'app-upload-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, Icon],
  template: `
    @if (uploads.total() > 0) {
      <section class="panel" aria-live="polite">
        <header class="head">
          <app-icon name="upload" />
          <strong i18n="@@upload.title">Загрузка файлов</strong>
          <span class="counts">
            {{ uploads.completed() }} / {{ uploads.total() }}
          </span>
          @if (uploads.isRunning()) {
            <button matButton type="button" (click)="uploads.cancel()" i18n="@@upload.cancel">Отменить</button>
          } @else {
            <button matButton type="button" (click)="uploads.dismissFinished()" i18n="@@upload.clear">
              Очистить
            </button>
          }
        </header>

        <mat-progress-bar mode="determinate" [value]="uploads.overallProgress()" />

        <ul class="tasks">
          @for (task of uploads.tasks(); track task.id) {
            <li class="task">
              <span class="task-name" [title]="task.name">{{ task.name }}</span>
              <span class="task-meta">
                @switch (task.status) {
                  @case ('pending') {
                    <span i18n="@@upload.pending">в очереди</span>
                  }
                  @case ('uploading') {
                    <span>{{ task.progress }}%</span>
                  }
                  @case ('done') {
                    <span class="ok" i18n="@@upload.done">готово</span>
                  }
                  @case ('canceled') {
                    <span i18n="@@upload.canceled">отменено</span>
                  }
                  @case ('error') {
                    <span class="bad">{{ task.error }}</span>
                  }
                }
                <span class="size">{{ size(task.size) }}</span>
              </span>
              @if (task.status === 'uploading') {
                <mat-progress-bar mode="determinate" [value]="task.progress" />
              }
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: `
    .panel {
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 0.75rem;
      padding: 0.75rem 1rem;
      margin-bottom: 1rem;
      background: var(--mat-sys-surface-container-low);
    }

    .head {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      margin-bottom: 0.5rem;
    }

    .counts {
      color: var(--mat-sys-on-surface-variant);
    }

    .tasks {
      list-style: none;
      margin: 0.75rem 0 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      max-height: 14rem;
      overflow: auto;
    }

    .task {
      display: grid;
      gap: 0.15rem;
    }

    .task-name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .task-meta {
      display: flex;
      gap: 0.75rem;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .ok {
      color: var(--mat-sys-primary);
    }

    .bad {
      color: var(--mat-sys-error);
    }
  `,
})
export class UploadPanel {
  protected readonly uploads = inject(UploadStore);
  private readonly locale = inject(LOCALE_ID);

  protected size(bytes: number): string {
    return formatFileSize(bytes, this.locale);
  }
}
