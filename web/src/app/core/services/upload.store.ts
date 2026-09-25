import {
  HttpClient,
  HttpEventType,
  HttpHeaders,
  HttpParams,
  HttpRequest,
  HttpXhrBackend,
} from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';

import { UploadResult } from '../models/api.models';
import { problemMessage, problemStatus } from '../http/problem';
import { SessionStore } from './session.store';

export interface UploadItem {
  readonly file: File;
  /** Path relative to the upload target; only set for folder uploads. */
  readonly relativePath: string | null;
}

export type UploadStatus = 'pending' | 'uploading' | 'done' | 'error' | 'canceled';

export interface UploadTask {
  readonly id: number;
  readonly name: string;
  readonly relativePath: string | null;
  readonly size: number;
  readonly progress: number;
  readonly status: UploadStatus;
  readonly error: string | null;
}

/**
 * Uploads files one request per file (as the API expects) and tracks progress per file.
 *
 * The application wide `HttpClient` uses the fetch backend, which cannot report upload progress:
 * `fetch()` has no upload stream. Uploads therefore go through a dedicated `HttpClient` bound to
 * the XHR backend, which does emit `HttpEventType.UploadProgress` events. Interceptors are not in
 * play on that path, so the same-origin marker and `withCredentials` are set explicitly.
 */
@Injectable()
export class UploadStore {
  private readonly backend = inject(HttpXhrBackend);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly client = new HttpClient(this.backend);

  private readonly tasksSignal = signal<readonly UploadTask[]>([]);
  private nextId = 1;
  private canceled = false;
  private current: Subscription | null = null;

  readonly tasks = this.tasksSignal.asReadonly();
  readonly isRunning = computed(() =>
    this.tasksSignal().some((task) => task.status === 'pending' || task.status === 'uploading'),
  );
  readonly total = computed(() => this.tasksSignal().length);
  readonly completed = computed(() => this.tasksSignal().filter((task) => task.status === 'done').length);
  readonly failed = computed(() => this.tasksSignal().filter((task) => task.status === 'error').length);
  readonly overallProgress = computed(() => {
    const tasks = this.tasksSignal();
    if (tasks.length === 0) {
      return 0;
    }

    const totalBytes = tasks.reduce((sum, task) => sum + Math.max(task.size, 1), 0);
    const uploadedBytes = tasks.reduce(
      (sum, task) => sum + (Math.max(task.size, 1) * task.progress) / 100,
      0,
    );

    return Math.round((uploadedBytes / totalBytes) * 100);
  });

  /** Uploads every item sequentially and resolves once the batch is finished. */
  async start(targetDirectory: string, items: readonly UploadItem[], overwrite: boolean): Promise<void> {
    if (items.length === 0) {
      return;
    }

    this.canceled = false;
    const queued = items.map((item) => {
      const task: UploadTask = {
        id: this.nextId++,
        name: item.relativePath ?? item.file.name,
        relativePath: item.relativePath,
        size: item.file.size,
        progress: 0,
        status: 'pending',
        error: null,
      };
      return { item, task };
    });

    this.tasksSignal.update((tasks) => [...tasks, ...queued.map((entry) => entry.task)]);

    for (const entry of queued) {
      if (this.canceled) {
        this.patch(entry.task.id, { status: 'canceled' });
        continue;
      }

      this.patch(entry.task.id, { status: 'uploading' });
      try {
        await this.uploadFile(entry.task.id, targetDirectory, entry.item, overwrite);
      } catch (error: unknown) {
        this.handleFailure(entry.task.id, error);
      } finally {
        this.current = null;
      }
    }
  }

  /** Stops the queue; the request that is currently in flight is aborted. */
  cancel(): void {
    this.canceled = true;
    this.current?.unsubscribe();
    this.current = null;
  }

  /** Removes finished entries from the panel. */
  dismissFinished(): void {
    this.tasksSignal.update((tasks) =>
      tasks.filter((task) => task.status === 'pending' || task.status === 'uploading'),
    );
  }

  private uploadFile(
    taskId: number,
    targetDirectory: string,
    item: UploadItem,
    overwrite: boolean,
  ): Promise<void> {
    const body = new FormData();
    body.append('file', item.file, item.file.name);
    if (item.relativePath !== null && item.relativePath.includes('/')) {
      body.append('relativePath', item.relativePath);
    }

    const request = new HttpRequest<FormData>('POST', '/api/fs/upload', body, {
      params: new HttpParams()
        .set('path', targetDirectory)
        .set('overwrite', overwrite ? 'true' : 'false'),
      reportProgress: true,
      withCredentials: true,
      headers: new HttpHeaders({ 'X-Requested-With': 'XMLHttpRequest' }),
    });

    return new Promise<void>((resolve, reject) => {
      this.current = this.client.request<UploadResult>(request).subscribe({
        next: (event) => {
          if (event.type === HttpEventType.UploadProgress) {
            const totalBytes = event.total ?? item.file.size;
            const percent = totalBytes > 0 ? Math.round((event.loaded / totalBytes) * 100) : 0;
            this.patch(taskId, { progress: Math.min(percent, 100) });
          } else if (event.type === HttpEventType.Response) {
            this.patch(taskId, { progress: 100, status: 'done' });
          }
        },
        error: (error: unknown) => reject(error instanceof Error ? error : new Error('upload failed')),
        complete: () => resolve(),
      });
    });
  }

  private handleFailure(taskId: number, error: unknown): void {
    if (problemStatus(error) === 401) {
      this.session.clear();
      void this.router.navigate(['/login'], { queryParams: { returnUrl: this.router.url } });
    }

    this.patch(taskId, {
      status: 'error',
      error: problemMessage(error, $localize`:@@upload.failed:Не удалось загрузить файл.`),
    });
  }

  private patch(id: number, changes: Partial<UploadTask>): void {
    this.tasksSignal.update((tasks) =>
      tasks.map((task) => (task.id === id ? { ...task, ...changes } : task)),
    );
  }
}
