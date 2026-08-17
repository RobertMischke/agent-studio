import { HttpErrorResponse, HttpRequest, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { AuthSessionState, AuthStatus } from './auth.service';
import { __resetPublicDemoGuardThrottle, publicDemoGuardInterceptor } from './public-demo-guard.interceptor';
import { HttpClient } from '@angular/common/http';

/**
 * AGT-W34 slice S4: the client-side read-only guard. It is explanatory UX in
 * front of the server edge, so what matters is that it refuses exactly the
 * mutating calls the edge would deny and never gets in the way of a read.
 */
describe('publicDemoGuardInterceptor', () => {
  const publicDemo: AuthStatus = {
    profile: 'public-demo',
    bootstrapRequired: false,
    authenticated: false,
    user: null,
  };

  let http: HttpClient;
  let controller: HttpTestingController;
  let auth: AuthSessionState;

  beforeEach(() => {
    __resetPublicDemoGuardThrottle();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([publicDemoGuardInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthSessionState);
  });

  it('refuses a mutating API call with the typed read-only denial', async () => {
    auth.status.set(publicDemo);

    const error = await new Promise<HttpErrorResponse>((resolve) => {
      http.post('/api/tasks', { title: 'probe' }).subscribe({ error: resolve });
    });

    expect(error.status).toBe(403);
    expect(error.error.error).toBe('public-demo-read-only');
    expect(error.error.readOnly).toBe(true);
    controller.verify();
  });

  it('lets reads through untouched', () => {
    auth.status.set(publicDemo);

    http.get('/api/tasks').subscribe();

    controller.expectOne((req: HttpRequest<unknown>) => req.url === '/api/tasks').flush([]);
    controller.verify();
  });

  it('stays out of the way outside the public demo profile', () => {
    auth.status.set({ ...publicDemo, profile: 'local' });

    http.post('/api/tasks', { title: 'probe' }).subscribe();

    controller.expectOne('/api/tasks').flush({});
    controller.verify();
  });
});
