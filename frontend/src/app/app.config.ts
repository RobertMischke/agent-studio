import { ApplicationConfig, ErrorHandler, provideBrowserGlobalErrorListeners, isDevMode } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideServiceWorker } from '@angular/service-worker';
import { provideCodingAgentChat } from '@coding-agent/chat';
import { CHAT_HISTORY_CONFIRM, PROJECT_CHAT_DATA_SOURCE } from '@coding-agent/chat/history';
import { ModalErrorHandler } from './services/error-dialog.service';
import { clientIdInterceptor } from './services/client-id.interceptor';
import { offlineGuardInterceptor } from './services/offline-guard.interceptor';
import { TaskReferenceNavigationService } from './services/task-reference-navigation.service';
import { MediaLightboxService } from './services/media-lightbox.service';
import { ConfirmDialogService } from './services/confirm-dialog.service';
import { ProjectChatDataSourceAdapter } from './services/project-chat-data-source.adapter';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([offlineGuardInterceptor, clientIdInterceptor])),
    { provide: ErrorHandler, useClass: ModalErrorHandler },
    // @coding-agent/chat host seams: markdown task-reference auto-linking and
    // click-to-enlarge images route into the app's existing root services.
    provideCodingAgentChat({
      taskReferences: TaskReferenceNavigationService,
      mediaLightbox: MediaLightboxService,
    }),
    // Virtualised chat-history list: loads through the existing project-chat
    // HTTP endpoints; destructive-ish confirmations reuse the app dialog.
    { provide: PROJECT_CHAT_DATA_SOURCE, useExisting: ProjectChatDataSourceAdapter },
    { provide: CHAT_HISTORY_CONFIRM, useExisting: ConfirmDialogService },
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
