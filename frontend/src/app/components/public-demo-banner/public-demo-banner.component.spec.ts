import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { PublicDemoBannerComponent } from './public-demo-banner.component';
import { PublicDemoService } from '../../services/public-demo.service';

const EDGE_STATUS = {
  active: true,
  readOnly: true,
  profile: 'public-demo-readonly',
  projects: ['demo-app', 'demo-platform'],
  allowlistDigest: 'sha256:abc',
  allowlistRouteCount: 44,
  maxRequestBodyBytes: 16384,
  requestsPerWindow: 240,
  windowSeconds: 60,
};

describe('PublicDemoBannerComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [PublicDemoBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    return {
      demo: TestBed.inject(PublicDemoService),
      http: TestBed.inject(HttpTestingController),
    };
  }

  it('explains the read-only boundary once the server reports the public demo profile', async () => {
    const { demo, http } = setup();
    demo.load();
    http.expectOne('/api/environment').flush({ publicDemo: EDGE_STATUS });

    const fixture = TestBed.createComponent(PublicDemoBannerComponent);
    await fixture.whenStable();

    const banner = fixture.nativeElement.querySelector('[data-testid="public-demo-banner"]') as HTMLElement;
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('Public demo');
    expect(banner.textContent).toContain('read-only');
    expect(demo.projects()).toEqual(['demo-app', 'demo-platform']);
    fixture.destroy();
    http.verify();
  });

  it('stays hidden in an ordinary installation', async () => {
    const { demo, http } = setup();
    demo.load();
    http.expectOne('/api/environment').flush({
      publicDemo: { ...EDGE_STATUS, active: false, readOnly: false, profile: 'local', projects: [] },
    });

    const fixture = TestBed.createComponent(PublicDemoBannerComponent);
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('[data-testid="public-demo-banner"]')).toBeNull();
    fixture.destroy();
    http.verify();
  });
});
