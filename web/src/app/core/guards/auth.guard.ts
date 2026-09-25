import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs';

import { NotificationService } from '../services/notification.service';
import { SessionStore } from '../services/session.store';

function toLogin(router: Router, returnUrl: string): UrlTree {
  return router.createUrlTree(['/login'], {
    queryParams: returnUrl.length > 0 && returnUrl !== '/' ? { returnUrl } : {},
  });
}

/** Requires an authenticated session; otherwise remembers the intended URL and redirects to login. */
export const authGuard: CanActivateFn = (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  return session
    .ensureLoaded()
    .pipe(map((profile) => (profile !== null ? true : toLogin(router, state.url))));
};

/** Requires an authenticated administrator. */
export const adminGuard: CanActivateFn = (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);
  const notifications = inject(NotificationService);

  return session.ensureLoaded().pipe(
    map((profile) => {
      if (profile === null) {
        return toLogin(router, state.url);
      }

      if (!profile.isAdmin) {
        notifications.error($localize`:@@users.adminOnly:Раздел доступен только администраторам.`);
        return router.createUrlTree(['/browse']);
      }

      return true;
    }),
  );
};
