import { HttpClient, HttpEvent, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  ConflictPolicy,
  DirectoryListing,
  TransferResult,
  UploadResult,
} from '../models/api.models';

/** Thin typed wrapper around the `/api/fs` endpoints. */
@Injectable({ providedIn: 'root' })
export class FileSystemApi {
  private readonly http = inject(HttpClient);

  list(path: string): Observable<DirectoryListing> {
    return this.http.get<DirectoryListing>('/api/fs/list', { params: new HttpParams().set('path', path) });
  }

  /** Absolute URL of the raw download; used for browser driven downloads. */
  downloadUrl(path: string): string {
    return `/api/fs/download?path=${encodeURIComponent(path)}`;
  }

  /** Absolute URL of the tar.gz archive of a folder. */
  archiveUrl(path: string): string {
    return `/api/fs/archive?path=${encodeURIComponent(path)}`;
  }

  mkdir(path: string): Observable<void> {
    return this.http.post<void>('/api/fs/mkdir', { path });
  }

  rename(path: string, newName: string): Observable<void> {
    return this.http.post<void>('/api/fs/rename', { path, newName });
  }

  copy(sources: readonly string[], destination: string, conflict: ConflictPolicy): Observable<TransferResult> {
    return this.http.post<TransferResult>('/api/fs/copy', { sources, destination, conflict });
  }

  move(sources: readonly string[], destination: string, conflict: ConflictPolicy): Observable<TransferResult> {
    return this.http.post<TransferResult>('/api/fs/move', { sources, destination, conflict });
  }

  delete(path: string, recursive: boolean): Observable<void> {
    return this.http.delete<void>('/api/fs/entry', {
      params: new HttpParams().set('path', path).set('recursive', recursive ? 'true' : 'false'),
    });
  }

  /**
   * Uploads a single file with progress reporting. `relativePath` is only sent for folder uploads
   * so the server can recreate the directory structure below the target folder.
   */
  upload(
    targetDirectory: string,
    file: File,
    relativePath: string | null,
    overwrite: boolean,
  ): Observable<HttpEvent<UploadResult>> {
    const body = new FormData();
    body.append('file', file, file.name);
    if (relativePath !== null && relativePath.includes('/')) {
      body.append('relativePath', relativePath);
    }

    return this.http.post<UploadResult>('/api/fs/upload', body, {
      params: new HttpParams()
        .set('path', targetDirectory)
        .set('overwrite', overwrite ? 'true' : 'false'),
      reportProgress: true,
      observe: 'events',
    });
  }
}
