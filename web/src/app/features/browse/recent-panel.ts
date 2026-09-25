import { ChangeDetectionStrategy, Component, LOCALE_ID, inject, input, output } from '@angular/core';

import { formatRelativeTime } from '../../core/format.utils';
import { HistoryItem } from '../../core/models/api.models';
import { Icon } from '../../shared/icon/icon';

/** "Recent folders" side panel fed by `/api/history`. */
@Component({
  selector: 'app-recent-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  template: `
    <nav class="recent" aria-labelledby="recent-title">
      <h2 id="recent-title" class="title">
        <app-icon name="history" />
        <span i18n="@@browse.recent">Недавние папки</span>
      </h2>

      @if (items().length === 0) {
        <p class="empty" i18n="@@browse.recentEmpty">История пока пуста.</p>
      } @else {
        <ul class="list">
          @for (item of items(); track item.path) {
            <li>
              <button
                type="button"
                class="entry"
                [class.current]="item.path === currentPath()"
                [title]="item.path"
                (click)="select.emit(item.path)"
              >
                <app-icon name="folder" [size]="16" />
                <span class="name">{{ item.path }}</span>
                <span class="meta">{{ relative(item.visitedUtc) }}</span>
              </button>
            </li>
          }
        </ul>
      }
    </nav>
  `,
  styles: `
    .recent {
      display: block;
      padding: 0.75rem 0.5rem;
    }

    .title {
      display: flex;
      align-items: center;
      gap: 0.4rem;
      font: var(--mat-sys-title-small);
      margin: 0 0 0.5rem 0.5rem;
    }

    .list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 0.15rem;
      max-height: calc(100vh - 12rem);
      overflow: auto;
    }

    .entry {
      display: grid;
      grid-template-columns: auto 1fr auto;
      align-items: center;
      gap: 0.4rem;
      width: 100%;
      padding: 0.4rem 0.5rem;
      border: none;
      border-radius: 0.5rem;
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }

    .entry:hover {
      background: var(--mat-sys-surface-container-high);
    }

    .entry.current {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }

    .name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .meta,
    .empty {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .empty {
      margin: 0 0.5rem;
    }
  `,
})
export class RecentPanel {
  readonly items = input.required<readonly HistoryItem[]>();
  readonly currentPath = input('');
  readonly select = output<string>();

  private readonly locale = inject(LOCALE_ID);

  protected relative(isoTimestamp: string): string {
    return formatRelativeTime(isoTimestamp, this.locale);
  }
}
