import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { bootstrapApplication } from '@angular/platform-browser';

import { EpicExpandHarnessComponent } from './app/epic-expand-harness.component';

bootstrapApplication(EpicExpandHarnessComponent, {
  providers: [provideZonelessChangeDetection(), provideHttpClient(), provideRouter([])],
}).catch((err) => console.error(err));
