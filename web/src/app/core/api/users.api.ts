import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  CreateUserRequest,
  HostUserDetails,
  HostUserList,
  UpdateUserRequest,
} from '../models/api.models';

/** Thin typed wrapper around the admin-only `/api/users` endpoints. */
@Injectable({ providedIn: 'root' })
export class UsersApi {
  private readonly http = inject(HttpClient);

  list(includeSystem = false): Observable<HostUserList> {
    return this.http.get<HostUserList>('/api/users', {
      params: new HttpParams().set('includeSystem', includeSystem ? 'true' : 'false'),
    });
  }

  groups(): Observable<readonly string[]> {
    return this.http.get<readonly string[]>('/api/users/groups');
  }

  details(name: string, paths: readonly string[] = []): Observable<HostUserDetails> {
    let params = new HttpParams();
    for (const path of paths) {
      params = params.append('paths', path);
    }

    return this.http.get<HostUserDetails>(`/api/users/${encodeURIComponent(name)}`, { params });
  }

  create(request: CreateUserRequest): Observable<HostUserDetails> {
    return this.http.post<HostUserDetails>('/api/users', request);
  }

  update(name: string, request: UpdateUserRequest): Observable<HostUserDetails> {
    return this.http.put<HostUserDetails>(`/api/users/${encodeURIComponent(name)}`, request);
  }

  delete(name: string, removeHome: boolean, revokePaths: readonly string[] = []): Observable<void> {
    let params = new HttpParams().set('removeHome', removeHome ? 'true' : 'false');
    for (const path of revokePaths) {
      params = params.append('revokePaths', path);
    }

    return this.http.delete<void>(`/api/users/${encodeURIComponent(name)}`, { params });
  }
}
