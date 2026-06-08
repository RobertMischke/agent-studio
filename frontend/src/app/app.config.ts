import { ApplicationConfig, ErrorHandler, provideBrowserGlobalErrorListeners, isDevMode } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideServiceWorker } from '@angular/service-worker';
import { ModalErrorHandler } from './services/error-dialog.service';
import { clientIdInterceptor } from './services/client-id.interceptor';
import { offlineGuardInterceptor } from './services/offline-guard.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([offlineGuardInterceptor, clientIdInterceptor])),
    { provide: ErrorHandler, useClass: ModalErrorHandler },
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
