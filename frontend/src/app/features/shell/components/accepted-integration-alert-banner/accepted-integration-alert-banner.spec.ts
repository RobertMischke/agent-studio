import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { AcceptedIntegrationAlertBannerComponent } from './accepted-integration-alert-banner';

describe('AcceptedIntegrationAlertBannerComponent', () => {
  it('caps the headline detail and links to the complete filtered board list', async () => {
    await TestBed.configureTestingModule({
      imports: [AcceptedIntegrationAlertBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(AcceptedIntegrationAlertBannerComponent);
    fixture.componentRef.setInput('projects', ['Agent Studio']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/pipeline/accepted-integration-alert').flush({
      active: true,
      stalledTaskCount: 13,
      thresholdMinutes: 30,
      oldestAcceptedAt: '2026-08-09T12:00:00Z',
      observedAt: '2026-08-09T13:00:00Z',
      items: [
        ...Array.from({ length: 12 }, (_, index) => ({
          taskKey: `AGT-${2600 + index}`, taskId: `task-${index}`, projectName: 'Agent Studio', title: `Task ${index}`,
          acceptedAt: '2026-08-09T12:00:00Z', integrationStatus: 'no-branch', lastOutcome: 'NoTaskBranch',
        })),
        {
          taskKey: 'OTH-1', taskId: 'other', projectName: 'Other', title: 'Other',
          acceptedAt: '2026-08-09T12:00:00Z', integrationStatus: 'pending', lastOutcome: 'Error',
        },
      ],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="accepted-integration-alert-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('12 accepted tasks have not reached successful integration for over 30 minutes');
    expect(banner.textContent).toContain('AGT-2600');
    expect(banner.textContent).toContain('AGT-2609');
    expect(banner.textContent).not.toContain('AGT-2610');
    expect(banner.textContent).toContain('and 2 more');
    expect(banner.textContent).not.toContain('OTH-1');
    const fullListLink = banner.querySelector('[data-testid="accepted-integration-full-list"]') as HTMLAnchorElement;
    expect(fullListLink.href).toContain('/board');
    expect(fullListLink.href).toContain('integration%3Astalled');
    fixture.destroy();
    http.verify();
  });
});
