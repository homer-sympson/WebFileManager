import { bootstrapApplication } from '@angular/platform-browser';

import { App } from './app/app';
import { appConfig } from './app/app.config';

bootstrapApplication(App, appConfig).catch((error: unknown) => {
  // Bootstrap failures are unrecoverable; surface them in the console for diagnostics.
  console.error(error);
});
