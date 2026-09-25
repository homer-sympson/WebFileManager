import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

/**
 * Starts browser downloads without buffering the file in memory: the API streams large files and
 * sets `Content-Disposition`, so a hidden anchor is both cheaper and faster than a blob download.
 */
@Injectable({ providedIn: 'root' })
export class DownloadService {
  private readonly document = inject(DOCUMENT);

  trigger(url: string): void {
    const anchor = this.document.createElement('a');
    anchor.href = url;
    anchor.rel = 'noopener';
    anchor.style.display = 'none';
    this.document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  }
}
