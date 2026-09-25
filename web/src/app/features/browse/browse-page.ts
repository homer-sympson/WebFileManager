import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  LOCALE_ID,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { Observable, Subscription, catchError, forkJoin, map, of } from 'rxjs';

import { FileSystemApi } from '../../core/api/fs.api';
import { HistoryApi } from '../../core/api/history.api';
import { formatDateTime, formatFileSize } from '../../core/format.utils';
import { problemMessage } from '../../core/http/problem';
import { effectiveAccessLabel, entryTypeLabel } from '../../core/labels';
import { ConflictPolicy, DirectoryListing, FileEntry, HistoryItem } from '../../core/models/api.models';
import {
  baseName,
  breadcrumbsFor,
  browseLink,
  isSameOrDescendant,
  joinPath,
  normalizePath,
  parentPath,
} from '../../core/path.utils';
import { CapabilitiesStore } from '../../core/services/capabilities.store';
import { ClipboardStore } from '../../core/services/clipboard.store';
import { CurrentUrlStore } from '../../core/services/current-url.store';
import { DownloadService } from '../../core/services/download.service';
import { NotificationService } from '../../core/services/notification.service';
import { UploadItem, UploadStore } from '../../core/services/upload.store';
import { Breadcrumb } from '../../core/path.utils';
import {
  ConflictDialog,
  ConflictDialogData,
  DeleteChoice,
  DeleteDialog,
  DeleteDialogData,
  PromptDialog,
  PromptDialogData,
  PropertiesDialog,
  PropertiesDialogData,
} from '../../shared/dialogs/dialogs';
import { Icon, IconName } from '../../shared/icon/icon';
import { RecentPanel } from './recent-panel';
import { UploadPanel } from './upload-panel';
import { itemsFromDataTransfer, itemsFromFileList } from './upload-items';

/** Flagship screen: directory listing, navigation, file operations and uploads. */
@Component({
  selector: 'app-browse-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [UploadStore],
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
    Icon,
    RecentPanel,
    UploadPanel,
  ],
  templateUrl: './browse-page.html',
  styleUrl: './browse-page.scss',
})
export class BrowsePage {
  private readonly fs = inject(FileSystemApi);
  private readonly history = inject(HistoryApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotificationService);
  private readonly download = inject(DownloadService);
  private readonly urlStore = inject(CurrentUrlStore);
  private readonly capabilities = inject(CapabilitiesStore);
  private readonly locale = inject(LOCALE_ID);

  protected readonly clipboard = inject(ClipboardStore);
  protected readonly uploads = inject(UploadStore);

  protected readonly path = computed(() => this.urlStore.browsePath());
  protected readonly crumbs = computed<readonly Breadcrumb[]>(() => breadcrumbsFor(this.path()));
  /** Server reported parent; `null` means the current folder is a browse root. */
  protected readonly parent = computed(() => {
    const listing = this.listingSignal();
    return listing === null ? parentPath(this.path()) : listing.parent;
  });

  private readonly listingSignal = signal<DirectoryListing | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly recent = signal<readonly HistoryItem[]>([]);
  protected readonly dropActive = signal(false);
  protected readonly selectedNames = signal<ReadonlySet<string>>(new Set<string>());
  protected readonly columns = ['select', 'name', 'size', 'modified', 'mode', 'owner', 'access'] as const;
  protected pathInput = '';

  protected readonly crumbsAriaLabel = $localize`:@@browse.crumbsAria:Навигация по пути`;
  protected readonly toolbarAriaLabel = $localize`:@@browse.toolbarAria:Действия с файлами`;
  protected readonly upLabel = $localize`:@@browse.up:Перейти в родительский каталог`;
  protected readonly refreshLabel = $localize`:@@browse.refresh:Обновить список`;
  protected readonly loadingLabel = $localize`:@@browse.loading:Загрузка списка`;
  protected readonly selectAllLabel = $localize`:@@browse.selectAll:Выбрать все объекты`;

  protected readonly entries = computed<readonly FileEntry[]>(() => {
    const listing = this.listingSignal();
    if (listing === null) {
      return [];
    }

    return [...listing.entries].sort((left, right) => {
      const leftRank = left.type === 'directory' ? 0 : 1;
      const rightRank = right.type === 'directory' ? 0 : 1;
      return leftRank === rightRank
        ? left.name.localeCompare(right.name, this.locale, { numeric: true })
        : leftRank - rightRank;
    });
  });

  protected readonly selectedEntries = computed<readonly FileEntry[]>(() => {
    const names = this.selectedNames();
    return this.entries().filter((entry) => names.has(entry.name));
  });

  protected readonly selectedCount = computed(() => this.selectedEntries().length);
  protected readonly allSelected = computed(
    () => this.entries().length > 0 && this.selectedCount() === this.entries().length,
  );
  protected readonly someSelected = computed(
    () => this.selectedCount() > 0 && this.selectedCount() < this.entries().length,
  );
  protected readonly singleSelection = computed<FileEntry | null>(() => {
    const selected = this.selectedEntries();
    return selected.length === 1 ? (selected[0] ?? null) : null;
  });
  protected readonly truncatedMessage = computed(() => {
    const listing = this.listingSignal();
    if (listing === null || !listing.truncated) {
      return null;
    }

    return $localize`:@@browse.truncated:Показаны не все объекты: в каталоге ${listing.total}:total: элементов, список ограничен.`;
  });

  private inFlight: Subscription | null = null;
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.inFlight?.unsubscribe();
    });

    effect(() => {
      const url = this.urlStore.url();

      // `/browse` carries no path: fall back to the first permitted browse root.
      if (url === '/browse' || url === '/browse/') {
        if (!this.capabilities.ready()) {
          this.capabilities.ensureLoaded().subscribe();
          return;
        }

        this.navigate(this.capabilities.browseRoots()[0] ?? '/', true);
        return;
      }

      this.load(this.path());
    });
  }

  // ---------------------------------------------------------------- navigation

  protected link(path: string): readonly string[] {
    return browseLink(path);
  }

  protected navigate(path: string, replaceUrl = false): void {
    void this.router.navigate(browseLink(path), { replaceUrl });
  }

  protected goToPath(): void {
    const normalized = normalizePath(this.pathInput);
    this.pathInput = normalized;
    this.navigate(normalized);
  }

  protected goUp(): void {
    const target = this.parent();
    if (target !== null) {
      this.navigate(target);
    }
  }

  protected open(entry: FileEntry): void {
    if (entry.type === 'file') {
      this.downloadEntry(entry);
      return;
    }

    this.navigate(entry.fullPath);
  }

  // ---------------------------------------------------------------- loading

  protected reload(): void {
    if (this.destroyed) {
      return;
    }

    this.load(this.path());
  }

  private load(path: string): void {
    if (this.destroyed) {
      return;
    }

    this.inFlight?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.selectedNames.set(new Set<string>());
    this.pathInput = path;

    this.inFlight = this.fs.list(path).subscribe({
      next: (listing) => {
        this.listingSignal.set(listing);
        this.loading.set(false);
        this.pathInput = listing.path;

        if (normalizePath(listing.path) !== normalizePath(path)) {
          this.navigate(listing.path, true);
        }

        this.loadRecent();
      },
      error: (error: unknown) => {
        this.listingSignal.set(null);
        this.loading.set(false);
        this.error.set(problemMessage(error, $localize`:@@browse.loadFailed:Не удалось открыть каталог.`));
      },
    });
  }

  private loadRecent(): void {
    this.history
      .list(20)
      .pipe(catchError(() => of<readonly HistoryItem[]>([])))
      .subscribe((items) => this.recent.set(items));
  }

  // ---------------------------------------------------------------- selection

  protected isSelected(name: string): boolean {
    return this.selectedNames().has(name);
  }

  protected toggle(name: string, checked: boolean): void {
    this.selectedNames.update((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(name);
      } else {
        next.delete(name);
      }

      return next;
    });
  }

  protected toggleAll(checked: boolean): void {
    this.selectedNames.set(checked ? new Set(this.entries().map((entry) => entry.name)) : new Set<string>());
  }

  // ---------------------------------------------------------------- row helpers

  protected iconFor(entry: FileEntry): IconName {
    switch (entry.type) {
      case 'directory':
        return 'folder';
      case 'symlink':
        return 'link';
      case 'file':
        return 'file';
      case 'other':
        return 'other';
    }
  }

  protected size(entry: FileEntry): string {
    return entry.type === 'directory' ? '—' : formatFileSize(entry.size, this.locale);
  }

  protected modified(entry: FileEntry): string {
    return formatDateTime(entry.lastWriteTimeUtc, this.locale);
  }

  protected access(entry: FileEntry): string {
    return effectiveAccessLabel(entry.effectiveAccess);
  }

  protected rowLabel(entry: FileEntry): string {
    return `${entryTypeLabel(entry.type)}: ${entry.name}`;
  }

  // ---------------------------------------------------------------- actions

  protected newFolder(): void {
    const data: PromptDialogData = {
      title: $localize`:@@browse.newFolderTitle:Новый каталог`,
      label: $localize`:@@browse.newFolderLabel:Имя каталога`,
      value: '',
      confirmLabel: $localize`:@@common.create:Создать`,
    };

    this.dialog
      .open(PromptDialog, { data, width: 'auto' })
      .afterClosed()
      .subscribe((name: string | null) => {
        if (name === null) {
          return;
        }

        this.mutate(
          this.fs.mkdir(joinPath(this.path(), name)),
          $localize`:@@browse.created:Каталог создан.`,
          $localize`:@@browse.createFailed:Не удалось создать каталог.`,
        );
      });
  }

  protected rename(): void {
    const entry = this.singleSelection();
    if (entry === null) {
      return;
    }

    const data: PromptDialogData = {
      title: $localize`:@@browse.renameTitle:Переименование`,
      label: $localize`:@@browse.renameLabel:Новое имя`,
      value: entry.name,
      confirmLabel: $localize`:@@common.rename:Переименовать`,
    };

    this.dialog
      .open(PromptDialog, { data, width: 'auto' })
      .afterClosed()
      .subscribe((name: string | null) => {
        if (name === null || name === entry.name) {
          return;
        }

        this.mutate(
          this.fs.rename(entry.fullPath, name),
          $localize`:@@browse.renamed:Объект переименован.`,
          $localize`:@@browse.renameFailed:Не удалось переименовать объект.`,
        );
      });
  }

  protected remove(): void {
    const selected = this.selectedEntries();
    if (selected.length === 0) {
      return;
    }

    const data: DeleteDialogData = {
      names: selected.map((entry) => entry.name),
      includesDirectories: selected.some((entry) => entry.type === 'directory'),
    };

    this.dialog
      .open(DeleteDialog, { data, width: 'auto' })
      .afterClosed()
      .subscribe((choice: DeleteChoice | null) => {
        if (choice === null) {
          return;
        }

        this.deleteEntries(selected, choice === 'recursive');
      });
  }

  private deleteEntries(entries: readonly FileEntry[], recursive: boolean): void {
    const failures: string[] = [];
    const requests = entries.map((entry) =>
      this.fs.delete(entry.fullPath, recursive).pipe(
        map(() => true),
        catchError((error: unknown) => {
          failures.push(`${entry.name}: ${problemMessage(error, $localize`:@@browse.deleteFailed:ошибка удаления`)}`);
          return of(false);
        }),
      ),
    );

    forkJoin(requests).subscribe((results) => {
      const removed = results.filter((ok) => ok).length;
      if (removed > 0) {
        this.notify.success($localize`:@@browse.deleted:Удалено объектов: ${removed}:count:.`);
      }

      if (failures.length > 0) {
        this.notify.error(failures.join('\n'));
      }

      this.reload();
    });
  }

  protected copySelection(): void {
    this.setClipboard('copy');
  }

  protected cutSelection(): void {
    this.setClipboard('cut');
  }

  private setClipboard(mode: 'copy' | 'cut'): void {
    const selected = this.selectedEntries();
    if (selected.length === 0) {
      return;
    }

    this.clipboard.set(
      mode,
      selected.map((entry) => entry.fullPath),
    );
    this.notify.info(
      mode === 'copy'
        ? $localize`:@@browse.copiedToClipboard:Скопировано в буфер: ${selected.length}:count:.`
        : $localize`:@@browse.cutToClipboard:Вырезано в буфер: ${selected.length}:count:.`,
    );
  }

  protected paste(): void {
    const clipboard = this.clipboard.state();
    if (clipboard === null) {
      return;
    }

    const destination = this.path();

    // Copying/moving a folder into itself would recurse forever.
    const invalid = clipboard.paths.find(
      (source) =>
        source === destination ||
        (clipboard.mode === 'copy' && isSameOrDescendant(source, destination)),
    );
    if (invalid !== undefined) {
      this.notify.error($localize`:@@browse.pasteIntoSelf:Нельзя вставить объект внутрь самого себя.`);
      return;
    }

    const sources = clipboard.paths.filter((source) => parentPath(source) !== destination);
    if (sources.length === 0) {
      this.notify.info($localize`:@@browse.pasteNoop:Объекты уже находятся в этом каталоге.`);
      return;
    }

    this.transfer(clipboard.mode, sources, destination);
  }

  private transfer(mode: 'copy' | 'cut', sources: readonly string[], destination: string): void {
    const existing = new Set(this.entries().map((entry) => entry.name));
    const conflicts = sources.filter((source) => existing.has(baseName(source)));

    if (conflicts.length === 0) {
      this.runTransfer(mode, sources, destination, 'fail');
      return;
    }

    const data: ConflictDialogData = {
      conflictingNames: conflicts.map((source) => baseName(source)),
      totalCount: sources.length,
    };

    this.dialog
      .open(ConflictDialog, { data, width: 'auto' })
      .afterClosed()
      .subscribe((policy: ConflictPolicy | null) => {
        if (policy !== null) {
          this.runTransfer(mode, sources, destination, policy);
        }
      });
  }

  private runTransfer(
    mode: 'copy' | 'cut',
    sources: readonly string[],
    destination: string,
    policy: ConflictPolicy,
  ): void {
    const request = mode === 'copy'
      ? this.fs.copy(sources, destination, policy)
      : this.fs.move(sources, destination, policy);

    request.subscribe({
      next: (result) => {
        if (result.skipped.length > 0) {
          this.notify.info(
            $localize`:@@browse.transferSkipped:Пропущено объектов: ${result.skipped.length}:count:.`,
          );
        } else {
          this.notify.success(
            mode === 'copy'
              ? $localize`:@@browse.copyDone:Скопировано объектов: ${result.affected.length}:count:.`
              : $localize`:@@browse.moveDone:Перемещено объектов: ${result.affected.length}:count:.`,
          );
        }

        if (mode === 'cut') {
          this.clipboard.clear();
        }

        this.reload();
      },
      error: (error: unknown) => {
        this.notify.error(
          problemMessage(
            error,
            mode === 'copy'
              ? $localize`:@@browse.copyFailed:Не удалось скопировать объекты.`
              : $localize`:@@browse.moveFailed:Не удалось переместить объекты.`,
          ),
        );
      },
    });
  }

  protected downloadEntry(entry: FileEntry | null = this.singleSelection()): void {
    if (entry === null) {
      return;
    }

    if (entry.type === 'directory') {
      this.download.trigger(this.fs.archiveUrl(entry.fullPath));
      return;
    }

    this.download.trigger(this.fs.downloadUrl(entry.fullPath));
  }

  protected showProperties(): void {
    const entry = this.singleSelection();
    if (entry === null) {
      return;
    }

    const data: PropertiesDialogData = { entry };
    this.dialog.open(PropertiesDialog, { data, width: 'auto' });
  }

  private mutate(request: Observable<unknown>, successMessage: string | null, failureFallback: string): void {
    request.subscribe({
      next: () => {
        if (successMessage !== null) {
          this.notify.success(successMessage);
        }

        this.reload();
      },
      error: (error: unknown) => this.notify.error(problemMessage(error, failureFallback)),
    });
  }

  // ---------------------------------------------------------------- uploads

  protected onFilesPicked(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files === null) {
      return;
    }

    this.beginUpload(itemsFromFileList(input.files, false));
    input.value = '';
  }

  protected onFolderPicked(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files === null) {
      return;
    }

    this.beginUpload(itemsFromFileList(input.files, true));
    input.value = '';
  }

  protected onDragOver(event: DragEvent): void {
    if (event.dataTransfer === null || !event.dataTransfer.types.includes('Files')) {
      return;
    }

    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    this.dropActive.set(true);
  }

  protected onDragLeave(event: DragEvent): void {
    event.preventDefault();
    this.dropActive.set(false);
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dropActive.set(false);

    const dataTransfer = event.dataTransfer;
    if (dataTransfer === null) {
      return;
    }

    void itemsFromDataTransfer(dataTransfer).then((items) => this.beginUpload(items));
  }

  private beginUpload(items: readonly UploadItem[]): void {
    if (items.length === 0) {
      return;
    }

    const existing = new Set(this.entries().map((entry) => entry.name));
    const conflicts = items.filter((item) => existing.has(this.uploadName(item)));

    if (conflicts.length === 0) {
      void this.runUpload(items, false);
      return;
    }

    const data: ConflictDialogData = {
      conflictingNames: conflicts.map((item) => this.uploadName(item)),
      totalCount: items.length,
    };

    this.dialog
      .open(ConflictDialog, { data, width: 'auto' })
      .afterClosed()
      .subscribe((policy: ConflictPolicy | null) => {
        if (policy === null) {
          return;
        }

        switch (policy) {
          case 'fail':
            this.notify.info($localize`:@@upload.aborted:Загрузка отменена.`);
            return;
          case 'overwrite':
            void this.runUpload(items, true);
            return;
          case 'skip':
            void this.runUpload(
              items.filter((item) => !existing.has(this.uploadName(item))),
              false,
            );
            return;
          case 'rename': {
            const taken = new Set(existing);
            const renamed = items.map((item) => {
              const name = this.uploadName(item);
              if (!taken.has(name)) {
                taken.add(name);
                return item;
              }

              const unique = makeUniqueName(name, taken);
              taken.add(unique);
              return { file: item.file, relativePath: unique };
            });
            void this.runUpload(renamed, false);
            return;
          }
        }
      });
  }

  private uploadName(item: UploadItem): string {
    return item.relativePath ?? item.file.name;
  }

  private async runUpload(items: readonly UploadItem[], overwrite: boolean): Promise<void> {
    await this.uploads.start(this.path(), items, overwrite);

    if (this.uploads.failed() === 0) {
      this.notify.success($localize`:@@upload.finished:Загрузка завершена.`);
    }

    this.reload();
  }
}

/** Appends ` (n)` before the extension until the name is free. */
function makeUniqueName(name: string, taken: ReadonlySet<string>): string {
  const dot = name.lastIndexOf('.');
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const extension = dot > 0 ? name.slice(dot) : '';

  for (let index = 1; index < 1000; index += 1) {
    const candidate = `${stem} (${index})${extension}`;
    if (!taken.has(candidate)) {
      return candidate;
    }
  }

  return `${stem} (${Date.now()})${extension}`;
}
