import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { AcceptedIntegrationAlertBannerComponent } from './accepted-integration-alert-banner';

describe('AcceptedIntegrationAlertBannerComponent', () => {
  it('shows stalled accepted cards for the visible project', async () => {
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
      stalledTaskCount: 2,
      thresholdMinutes: 30,
      oldestAcceptedAt: '2026-08-09T12:00:00Z',
      observedAt: '2026-08-09T13:00:00Z',
      items: [
        {
          taskKey: 'AGT-2531', taskId: 'one', projectName: 'Agent Studio', title: 'One',
          acceptedAt: '2026-08-09T12:00:00Z', integrationStatus: 'no-branch', lastOutcome: 'NoTaskBranch',
        },
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
    expect(banner.textContent).toContain('1 accepted task has not reached successful integration for over 30 minutes');
    expect(banner.textContent).toContain('Agent Studio: AGT-2531');
    expect(banner.textContent).not.toContain('OTH-1');
    fixture.destroy();
    http.verify();
  });
});
