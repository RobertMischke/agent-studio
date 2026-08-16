import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { publicDemoReadOnlyInterceptor } from './public-demo-readonly.interceptor';
import { PublicDemoService } from './public-demo.service';

describe('publicDemoReadOnlyInterceptor', () => {
  function setup(readOnly: boolean) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([publicDemoReadOnlyInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    const demo = TestBed.inject(PublicDemoService);
    const http = TestBed.inject(HttpTestingController);
    // The read-only flag is server-owned; the bootstrap read is what arms it.
    demo.load();
    http.expectOne('/api/environment').flush({
      publicDemo: {
        active: readOnly,
        readOnly,
        profile: readOnly ? 'public-demo-readonly' : 'local',
        projects: [],
        allowlistDigest: 'sha256:abc',
        allowlistRouteCount: 44,
        maxRequestBodyBytes: 16384,
        requestsPerWindow: 240,
        windowSeconds: 60,
      },
    });
    return { client: TestBed.inject(HttpClient), http };
  }

  it('refuses mutating calls with the same code the edge returns', async () => {
    const { client, http } = setup(true);

    const error = await new Promise<{ status: number; error: { error: string } }>((resolve) => {
      client.post('/api/tasks', {}).subscribe({ error: resolve });
    });

    expect(error.status).toBe(403);
    expect(error.error.error).toBe('public-demo-read-only');
    http.verify();
  });

  it('leaves reads and the reference-status batch alone', async () => {
    const { client, http } = setup(true);

    client.get('/api/tasks').subscribe();
    client.post('/api/tasks/reference-status', {}).subscribe();

    http.expectOne('/api/tasks').flush([]);
    http.expectOne('/api/tasks/reference-status').flush({});
    http.verify();
  });

  it('does not interfere with an ordinary installation', async () => {
    const { client, http } = setup(false);

    client.post('/api/tasks', {}).subscribe();

    http.expectOne('/api/tasks').flush({});
    http.verify();
  });
});
