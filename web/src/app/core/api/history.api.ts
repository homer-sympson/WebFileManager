import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { HistoryItem } from '../models/api.models';

/** Thin typed wrapper around the `/api/history` endpoints. */
@Injectable({ providedIn: 'root' })
export class HistoryApi {
  private readonly http = inject(HttpClient);

  list(limit = 100): Observable<readonly HistoryItem[]> {
    return this.http.get<readonly HistoryItem[]>('/api/history', {
      params: new HttpParams().set('limit', limit),
    });
  }

  record(path: string): Observable<void> {
    return this.http.post<void>('/api/history', { path });
  }

  clear(): Observable<void> {
    return this.http.delete<void>('/api/history');
  }
}
