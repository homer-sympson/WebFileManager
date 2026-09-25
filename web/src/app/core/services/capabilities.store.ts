import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, finalize, of, shareReplay, tap } from 'rxjs';

import { SystemApi } from '../api/system.api';
import { Capabilities } from '../models/api.models';

/** Caches `/api/system/capabilities`, which the login and profile screens both need. */
@Injectable({ providedIn: 'root' })
export class CapabilitiesStore {
  private readonly api = inject(SystemApi);

  private readonly valueSignal = signal<Capabilities | null>(null);
  private readonly readySignal = signal(false);
  private inFlight: Observable<Capabilities | null> | null = null;

  readonly capabilities = this.valueSignal.asReadonly();
  /** True once the first attempt finished, successfully or not. */
  readonly ready = this.readySignal.asReadonly();
  readonly aclAvailable = computed(() => this.valueSignal()?.aclAvailable === true);
  readonly browseRoots = computed<readonly string[]>(() => this.valueSignal()?.browseRoots ?? []);

  ensureLoaded(): Observable<Capabilities | null> {
    const cached = this.valueSignal();
    if (cached !== null) {
      return of(cached);
    }

    if (this.inFlight !== null) {
      return this.inFlight;
    }

    this.inFlight = this.api.capabilities().pipe(
      tap((capabilities) => this.valueSignal.set(capabilities)),
      catchError(() => of<Capabilities | null>(null)),
      finalize(() => {
        this.readySignal.set(true);
        this.inFlight = null;
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    return this.inFlight;
  }
}
