import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { bootstrapApplication } from '@angular/platform-browser';

import { WaitsOnGalleryComponent } from './app/waits-on-gallery.component';

bootstrapApplication(WaitsOnGalleryComponent, {
  providers: [provideZonelessChangeDetection(), provideHttpClient(), provideRouter([])],
}).catch((err) => console.error(err));
