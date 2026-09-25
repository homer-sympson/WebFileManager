import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';

import { problemCode } from './problem';
import { sessionEndedNotice } from '../session-messages';
import { SessionStore } from '../services/session.store';
import { TabIdentity } from '../services/tab-identity';

const MUTATING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function targetsApi(url: string): boolean {
  return url.startsWith('/api/') || url.startsWith('api/');
}

/**
 * Adds the same-origin marker required by the backend for every state changing request and keeps
 * the session cookie attached to same-origin API calls.
 */
export const apiRequestInterceptor: HttpInterceptorFn = (request, next) => {
  if (!targetsApi(request.url)) {
    return next(request);
  }

  const headers: Record<string, string> = {};
  if (MUTATING_METHODS.has(request.method.toUpperCase())) {
    headers['X-Requested-With'] = 'XMLHttpRequest';
  }

  // Identifies the tab so a second tab of the same browser is refused instead of sharing the session.
  if (!request.url.includes('/api/auth/page-closed')) {
    headers['X-Tab-Id'] = inject(TabIdentity).tabId;
  }

  return next(request.clone({ setHeaders: headers, withCredentials: true }));
};

/**
 * A 401 from any API call means the server side session is gone: drop the cached profile and send
 * the user back to the login page, remembering where they were.
 */
export const unauthorizedInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && targetsApi(request.url)) {
        // A rejected sign-in is not an ended session: the login form reports that itself.
        const isSignInAttempt = request.url.includes('/api/auth/login');
        if (!isSignInAttempt) {
          const notice = sessionEndedNotice(error);
          if (notice !== null) {
            session.noteEnded(notice);
          }

          session.clear();
          if (!router.url.startsWith('/login')) {
            void router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
          }
        }
      }

      if (
        error instanceof HttpErrorResponse &&
        error.status === 409 &&
        problemCode(error) === 'tab-conflict'
      ) {
        // Another tab owns the session: block this tab instead of logging the whole browser out.
        session.noteTabConflict();
      }

      return throwError(() => error);
    }),
  );
};
