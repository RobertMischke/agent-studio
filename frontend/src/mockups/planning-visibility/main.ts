import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { bootstrapApplication } from '@angular/platform-browser';
import { of } from 'rxjs';

import { TaskService } from '../../app/services/task.service';
import { PlanningVisibilityGalleryComponent } from './app/planning-visibility-gallery.component';

// Backend-free harness: the real PlanningSpawnPanelComponent injects TaskService
// (which owns the SignalR jobs hub) only to write the "no follow-up intended"
// declaration on click. Nothing here clicks it, so we swap in a tiny stub so the
// real SignalR/poll machinery never spins up. Every visible state is a pure
// function of the seeded `job.planningSpawn`, matching the precedent set by the
// other src/mockups/* galleries (no services, HTTP, or SignalR at render time).
const taskServiceStub = {
  setPlanningClosure: () => of(null),
} as unknown as TaskService;

bootstrapApplication(PlanningVisibilityGalleryComponent, {
  providers: [
    provideZonelessChangeDetection(),
    provideHttpClient(),
    provideRouter([]),
    { provide: TaskService, useValue: taskServiceStub },
  ],
}).catch((err) => console.error(err));
