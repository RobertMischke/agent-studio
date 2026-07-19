import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { bootstrapApplication } from '@angular/platform-browser';

import { TaskCardDeleteHarnessComponent } from './app/task-card-delete-harness.component';

bootstrapApplication(TaskCardDeleteHarnessComponent, {
  providers: [provideZonelessChangeDetection(), provideHttpClient(), provideRouter([])],
}).catch((err) => console.error(err));
