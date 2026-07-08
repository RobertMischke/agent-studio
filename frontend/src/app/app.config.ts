import { ApplicationConfig, ErrorHandler, provideBrowserGlobalErrorListeners, isDevMode } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideServiceWorker } from '@angular/service-worker';
import { provideCodingAgentChat } from 'coding-agent-chat';
import { ModalErrorHandler } from './services/error-dialog.service';
import { clientIdInterceptor } from './services/client-id.interceptor';
import { offlineGuardInterceptor } from './services/offline-guard.interceptor';
import { TaskReferenceNavigationService } from './services/task-reference-navigation.service';
import { MediaLightboxService } from './services/media-lightbox.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([offlineGuardInterceptor, clientIdInterceptor])),
    { provide: ErrorHandler, useClass: ModalErrorHandler },
    // coding-agent-chat host seams: markdown task-reference auto-linking and
    // click-to-enlarge images route into the app's existing root services.
    provideCodingAgentChat({
      taskReferences: TaskReferenceNavigationService,
      mediaLightbox: MediaLightboxService,
    }),
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
