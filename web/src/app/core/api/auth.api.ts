import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { LoginOptions, LoginRequest, UserProfile } from '../models/api.models';

/** Thin typed wrapper around the `/api/auth` endpoints. */
@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly http = inject(HttpClient);

  login(credentials: LoginRequest): Observable<UserProfile> {
    return this.http.post<UserProfile>('/api/auth/login', credentials);
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', null);
  }

  me(): Observable<UserProfile> {
    return this.http.get<UserProfile>('/api/auth/me');
  }

  loginOptions(): Observable<LoginOptions> {
    return this.http.get<LoginOptions>('/api/auth/login-options');
  }
}
