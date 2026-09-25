import { Routes } from '@angular/router';

import { adminGuard, authGuard } from './core/guards/auth.guard';
import { AppShell } from './layout/app-shell';

export const routes: Routes = [
  {
    path: 'login',
    title: $localize`:@@route.login:Вход`,
    loadComponent: () => import('./features/login/login-page').then((module) => module.LoginPage),
  },
  {
    path: '',
    component: AppShell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'browse' },
      {
        path: 'browse',
        title: $localize`:@@route.browse:Файлы`,
        loadComponent: () => import('./features/browse/browse-page').then((module) => module.BrowsePage),
      },
      {
        // The folder path is carried in the URL so deep links and history navigation work.
        path: 'browse/**',
        title: $localize`:@@route.browse:Файлы`,
        loadComponent: () => import('./features/browse/browse-page').then((module) => module.BrowsePage),
      },
      {
        path: 'history',
        title: $localize`:@@route.history:История`,
        loadComponent: () => import('./features/history/history-page').then((module) => module.HistoryPage),
      },
      {
        path: 'profile',
        title: $localize`:@@route.profile:Профиль`,
        loadComponent: () => import('./features/profile/profile-page').then((module) => module.ProfilePage),
      },
      {
        path: 'users',
        title: $localize`:@@route.users:Пользователи`,
        canActivate: [adminGuard],
        loadComponent: () => import('./features/users/user-list-page').then((module) => module.UserListPage),
      },
      {
        path: 'users/new',
        title: $localize`:@@route.userNew:Новый пользователь`,
        canActivate: [adminGuard],
        loadComponent: () => import('./features/users/user-form-page').then((module) => module.UserFormPage),
      },
      {
        path: 'users/:name',
        title: $localize`:@@route.userEdit:Пользователь`,
        canActivate: [adminGuard],
        loadComponent: () => import('./features/users/user-form-page').then((module) => module.UserFormPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
