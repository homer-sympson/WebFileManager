import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';

export type NotificationKind = 'success' | 'error' | 'info';

/** Snackbar facade so every feature reports problems the same way. */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly snackBar = inject(MatSnackBar);

  success(message: string): void {
    this.show(message, 'success');
  }

  info(message: string): void {
    this.show(message, 'info');
  }

  /** Errors stay visible longer than confirmations. */
  error(message: string): void {
    this.show(message, 'error');
  }

  private show(message: string, kind: NotificationKind): void {
    this.snackBar.open(message, $localize`:@@common.dismiss:Закрыть`, {
      duration: kind === 'error' ? 8000 : 4000,
      horizontalPosition: 'center',
      verticalPosition: 'bottom',
      panelClass: [`snack-${kind}`],
    });
  }
}
