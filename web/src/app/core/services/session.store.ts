import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, finalize, of, shareReplay, tap } from 'rxjs';

import { AuthApi } from '../api/auth.api';
import { UserProfile } from '../models/api.models';

export type SessionStatus = 'unknown' | 'loading' | 'ready';

/** Why a previously valid session is no longer usable (single session policy, expiry, logout...). */
export interface SessionEndedNotice {
  readonly code: string | null;
  readonly detail: string | null;
}

/**
 * Holds the signed in profile. The session cookie itself is HttpOnly, so the only way to know
 * whether we are authenticated is to ask `/api/auth/me` once and cache the answer.
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly auth = inject(AuthApi);

  private readonly profileSignal = signal<UserProfile | null>(null);
  private readonly statusSignal = signal<SessionStatus>('unknown');
  private readonly endedSignal = signal<SessionEndedNotice | null>(null);
  private readonly tabConflictSignal = signal(false);
  private inFlight: Observable<UserProfile | null> | null = null;

  readonly profile = this.profileSignal.asReadonly();
  readonly status = this.statusSignal.asReadonly();

  /** Reason of the last server side session termination, shown on the login page. */
  readonly ended = this.endedSignal.asReadonly();

  /** True when another tab of this browser owns the session; the UI blocks itself in that case. */
  readonly tabConflict = this.tabConflictSignal.asReadonly();
  readonly isAuthenticated = computed(() => this.profileSignal() !== null);
  readonly isAdmin = computed(() => this.profileSignal()?.isAdmin === true);
  readonly displayName = computed(() => {
    const profile = this.profileSignal();
    if (profile === null) {
      return '';
    }

    return profile.fullName.trim().length > 0 ? profile.fullName : profile.userName;
  });

  /** Loads the profile once; concurrent callers share a single request. */
  ensureLoaded(force = false): Observable<UserProfile | null> {
    if (!force && this.statusSignal() === 'ready') {
      return of(this.profileSignal());
    }

    if (this.inFlight !== null) {
      return this.inFlight;
    }

    this.statusSignal.set('loading');
    this.inFlight = this.auth.me().pipe(
      tap((profile) => {
        this.profileSignal.set(profile);
        this.statusSignal.set('ready');
      }),
      catchError(() => {
        this.clear();
        return of<UserProfile | null>(null);
      }),
      finalize(() => {
        this.inFlight = null;
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    return this.inFlight;
  }

  login(userName: string, password: string): Observable<UserProfile> {
    return this.auth.login({ userName, password }).pipe(
      tap((profile) => {
        this.profileSignal.set(profile);
        this.statusSignal.set('ready');
        this.endedSignal.set(null);
        this.inFlight = null;
      }),
    );
  }

  logout(): Observable<void> {
    return this.auth.logout().pipe(
      catchError(() => of(undefined)),
      tap(() => {
        this.endedSignal.set(null);
        this.clear();
      }),
    );
  }

  /** Remembers that the server closed the session, so the login page can explain why. */
  noteEnded(notice: SessionEndedNotice): void {
    if (notice.code === null && notice.detail === null) {
      return;
    }

    this.endedSignal.set(notice);
  }

  dismissEnded(): void {
    this.endedSignal.set(null);
  }

  /** Called when the server refuses this tab because another tab owns the session. */
  noteTabConflict(): void {
    this.tabConflictSignal.set(true);
  }

  /** Drops the cached profile; the next guard run will re-probe the server. */
  clear(): void {
    this.profileSignal.set(null);
    this.statusSignal.set('ready');
    this.inFlight = null;
  }
}
