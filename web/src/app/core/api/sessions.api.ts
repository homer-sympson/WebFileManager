import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** One live session as reported to administrators. */
export interface ActiveSession {
  readonly userId: number;
  readonly userName: string;
  readonly createdUtc: string;
  readonly lastSeenUtc: string;
  readonly expiresUtc: string;
  readonly remoteIp: string | null;
  readonly userAgent: string | null;
  readonly hasActiveTab: boolean;
  readonly pendingPageClose: boolean;
  readonly isCurrentSession: boolean;
}

export interface ActiveSessionList {
  readonly sessions: readonly ActiveSession[];
}

/**
 * Admin-only session oversight. Closing a session is the escape hatch for a browser that crashed
 * without sending the page-close beacon and would otherwise keep the account locked.
 */
@Injectable({ providedIn: 'root' })
export class SessionsApi {
  private readonly http = inject(HttpClient);

  list(): Observable<ActiveSessionList> {
    return this.http.get<ActiveSessionList>('/api/sessions');
  }

  close(userName: string): Observable<void> {
    return this.http.delete<void>(`/api/sessions/${encodeURIComponent(userName)}`);
  }
}
