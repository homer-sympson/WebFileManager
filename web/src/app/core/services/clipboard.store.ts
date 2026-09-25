import { Injectable, computed, signal } from '@angular/core';

export type ClipboardMode = 'copy' | 'cut';

export interface ClipboardState {
  readonly mode: ClipboardMode;
  readonly paths: readonly string[];
}

/** In-app file clipboard shared by the toolbar actions on the browse screen. */
@Injectable({ providedIn: 'root' })
export class ClipboardStore {
  private readonly stateSignal = signal<ClipboardState | null>(null);

  readonly state = this.stateSignal.asReadonly();
  readonly count = computed(() => this.stateSignal()?.paths.length ?? 0);
  readonly mode = computed<ClipboardMode | null>(() => this.stateSignal()?.mode ?? null);
  readonly paths = computed<readonly string[]>(() => this.stateSignal()?.paths ?? []);

  set(mode: ClipboardMode, paths: readonly string[]): void {
    this.stateSignal.set(paths.length === 0 ? null : { mode, paths: [...paths] });
  }

  clear(): void {
    this.stateSignal.set(null);
  }
}
