import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { bootstrapApplication } from '@angular/platform-browser';

import { WorkflowLanesGalleryComponent } from './app/workflow-lanes-gallery.component';

bootstrapApplication(WorkflowLanesGalleryComponent, {
  providers: [provideZonelessChangeDetection(), provideHttpClient()],
}).catch((err) => console.error(err));
