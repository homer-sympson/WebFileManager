import { Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';

import { pathFromBrowseUrl } from '../path.utils';

/**
 * Tracks the active router URL so components can derive state from it.
 *
 * The browse screen carries the absolute folder path in the URL (`/browse/tmp/demo`) so that deep
 * links and browser back/forward navigation work; the path is parsed from the URL rather than from
 * a route parameter so that paths of any depth and with encoded characters work.
 */
@Injectable({ providedIn: 'root' })
export class CurrentUrlStore {
  private readonly router = inject(Router);
  private readonly urlSignal = signal(this.router.url);

  readonly url = this.urlSignal.asReadonly();
  /** Absolute path shown by the browse screen. */
  readonly browsePath = computed(() => pathFromBrowseUrl(this.urlSignal()));

  constructor() {
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((event) => this.urlSignal.set(event.urlAfterRedirects));
  }
}
