import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { Capabilities } from '../models/api.models';

/** Thin typed wrapper around the `/api/system` endpoints. */
@Injectable({ providedIn: 'root' })
export class SystemApi {
  private readonly http = inject(HttpClient);

  capabilities(): Observable<Capabilities> {
    return this.http.get<Capabilities>('/api/system/capabilities');
  }
}
