import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { applyDevModeIfFlagged } from './dev-mode';

applyDevModeIfFlagged()
  .finally(() => bootstrapApplication(App, appConfig).catch((err) => console.error(err)));
