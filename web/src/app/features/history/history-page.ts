import { ChangeDetectionStrategy, Component, LOCALE_ID, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { Router } from '@angular/router';

import { HistoryApi } from '../../core/api/history.api';
import { formatDateTime, formatRelativeTime } from '../../core/format.utils';
import { problemMessage } from '../../core/http/problem';
import { HistoryItem } from '../../core/models/api.models';
import { browseLink } from '../../core/path.utils';
import { NotificationService } from '../../core/services/notification.service';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/dialogs/dialogs';
import { Icon } from '../../shared/icon/icon';
import { MatDialog } from '@angular/material/dialog';

/** Paginated list of recently visited folders. */
@Component({
  selector: 'app-history-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatTableModule, MatPaginatorModule, MatButtonModule, MatProgressBarModule, Icon],
  template: `
    <div class="page">
      <header class="page-header">
        <h1 i18n="@@history.title">История посещений</h1>
        <span class="spacer"></span>
        <button matButton="outlined" type="button" [disabled]="items().length === 0" (click)="clearHistory()">
          <app-icon name="delete" />
          <span i18n="@@history.clear">Очистить историю</span>
        </button>
      </header>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }

      @if (error(); as message) {
        <div class="banner error" role="alert">
          <app-icon name="warning" />
          <span>{{ message }}</span>
          <button matButton type="button" (click)="load()" i18n="@@common.retry">Повторить</button>
        </div>
      }

      @if (items().length === 0 && !loading()) {
        <div class="state-message">
          <app-icon name="history" [size]="40" />
          <p i18n="@@history.empty">История пуста.</p>
        </div>
      } @else {
        <table mat-table [dataSource]="pageItems()" class="history-table">
          <ng-container matColumnDef="path">
            <th mat-header-cell *matHeaderCellDef i18n="@@history.colPath">Каталог</th>
            <td mat-cell *matCellDef="let item">
              <button type="button" class="link" (click)="open(item)">{{ item.path }}</button>
            </td>
          </ng-container>

          <ng-container matColumnDef="visited">
            <th mat-header-cell *matHeaderCellDef i18n="@@history.colVisited">Посещён</th>
            <td mat-cell *matCellDef="let item" [title]="visitedExact(item)">
              {{ visited(item) }}
            </td>
          </ng-container>

          <ng-container matColumnDef="count">
            <th mat-header-cell *matHeaderCellDef i18n="@@history.colCount">Визитов</th>
            <td mat-cell *matCellDef="let item">{{ item.visitCount }}</td>
          </ng-container>

          <ng-container matColumnDef="actions">
            <th mat-header-cell *matHeaderCellDef i18n="@@history.colActions">Действия</th>
            <td mat-cell *matCellDef="let item">
              <button matButton type="button" (click)="open(item)" i18n="@@history.open">Открыть</button>
            </td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>

        <mat-paginator
          [length]="items().length"
          [pageSize]="pageSize()"
          [pageIndex]="pageIndex()"
          [pageSizeOptions]="[10, 25, 50, 100]"
          (page)="onPage($event)"
          i18n-aria-label="@@history.paginatorAria"
          aria-label="Постраничная навигация по истории"
        />
      }
    </div>
  `,
  styles: `
    .history-table {
      width: 100%;
    }

    .link {
      padding: 0;
      border: none;
      background: transparent;
      color: var(--mat-sys-primary);
      font: inherit;
      text-align: left;
      cursor: pointer;
      overflow-wrap: anywhere;
    }

    .link:hover {
      text-decoration: underline;
    }

    .banner {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.6rem 0.75rem;
      border-radius: 0.6rem;
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
      margin-bottom: 0.75rem;
    }
  `,
})
export class HistoryPage {
  private readonly history = inject(HistoryApi);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotificationService);
  private readonly locale = inject(LOCALE_ID);

  protected readonly columns = ['path', 'visited', 'count', 'actions'] as const;
  protected readonly items = signal<readonly HistoryItem[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly pageIndex = signal(0);
  protected readonly pageSize = signal(10);

  protected readonly pageItems = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.items().slice(start, start + this.pageSize());
  });

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.history.list(200).subscribe({
      next: (items) => {
        this.items.set(items);
        this.loading.set(false);
        this.clampPage();
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.error.set(problemMessage(error, $localize`:@@history.loadFailed:Не удалось загрузить историю.`));
      },
    });
  }

  protected onPage(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
  }

  protected visited(item: HistoryItem): string {
    return formatRelativeTime(item.visitedUtc, this.locale);
  }

  protected visitedExact(item: HistoryItem): string {
    return formatDateTime(item.visitedUtc, this.locale);
  }

  protected open(item: HistoryItem): void {
    void this.router.navigate(browseLink(item.path));
  }

  protected clearHistory(): void {
    const data: ConfirmDialogData = {
      title: $localize`:@@history.clearTitle:Очистка истории`,
      message: $localize`:@@history.clearMessage:Удалить всю историю посещений? Действие необратимо.`,
      confirmLabel: $localize`:@@history.clearConfirm:Очистить`,
      danger: true,
    };

    this.dialog
      .open(ConfirmDialog, { data, width: 'auto' })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed !== true) {
          return;
        }

        this.history.clear().subscribe({
          next: () => {
            this.items.set([]);
            this.pageIndex.set(0);
            this.notify.success($localize`:@@history.cleared:История очищена.`);
          },
          error: (error: unknown) =>
            this.notify.error(problemMessage(error, $localize`:@@history.clearFailed:Не удалось очистить историю.`)),
        });
      });
  }

  private clampPage(): void {
    const lastIndex = Math.max(0, Math.ceil(this.items().length / this.pageSize()) - 1);
    if (this.pageIndex() > lastIndex) {
      this.pageIndex.set(lastIndex);
    }
  }
}
