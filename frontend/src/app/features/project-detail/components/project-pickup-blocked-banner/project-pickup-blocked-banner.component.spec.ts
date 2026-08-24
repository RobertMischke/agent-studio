import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import {
  ProjectPickupBlockedBannerComponent,
  type ProjectPickupGateSummary,
} from './project-pickup-blocked-banner.component';

async function render(gate: ProjectPickupGateSummary) {
  await TestBed.configureTestingModule({
    imports: [ProjectPickupBlockedBannerComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectPickupBlockedBannerComponent);
  fixture.componentRef.setInput('gate', gate);
  fixture.detectChanges();
  return fixture;
}

describe('ProjectPickupBlockedBannerComponent', () => {
  it('names the pile of ready cards a shut build-profile gate is holding back', async () => {
    const fixture = await render({
      pickupAllowed: false,
      gateReason: 'build profile declared but not yet validated (no green dry-run)',
      gateReasonCode: 'not-validated',
      validationWorkspace: '/srv/projects/quality-studio',
      readyCardCount: 25,
    });

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="project-pickup-blocked-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('25 ready cards are not claimable: build profile not validated');
    // The headline states the class of problem; the detail states the exact
    // cause without repeating it as a second, lowercase sentence.
    expect(banner.textContent).toContain(
      'Gate reason: build profile declared but not yet validated (no green dry-run)');
    expect(banner.textContent).toContain('/srv/projects/quality-studio');
    fixture.destroy();
  });

  it('still warns when the project happens to have no ready cards right now', async () => {
    const fixture = await render({
      pickupAllowed: false,
      gateReason: 'last validation dry-run failed: build exited 1',
      gateReasonCode: 'validation-failed',
      validationWorkspace: null,
    });

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="project-pickup-blocked-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('Auto-pickup is blocked: build profile not validated');
    expect(banner.textContent).toContain('build exited 1');
    expect(banner.textContent).toContain('green build/test gate');
    fixture.destroy();
  });

  it('uses the singular for a single held card', async () => {
    const fixture = await render({
      pickupAllowed: false,
      gateReason: 'validation dry-run in progress',
      gateReasonCode: 'validating',
      readyCardCount: 1,
    });

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="project-pickup-blocked-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('1 ready card is not claimable');
    fixture.destroy();
  });

  it('renders nothing while the gate is open', async () => {
    const fixture = await render({
      pickupAllowed: true,
      gateReason: 'pipeline-ready',
      gateReasonCode: 'pipeline-ready',
      readyCardCount: 25,
    });

    expect(fixture.nativeElement.querySelector('[data-testid="project-pickup-blocked-banner"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="project-pickup-revalidation-banner"]')).toBeNull();
    fixture.destroy();
  });

  it('counts down the revalidation grace instead of claiming everything is fine', async () => {
    const fixture = await render({
      pickupAllowed: true,
      gateReason: 'build profile edited after a green validation; revalidation pending (2 run(s) of grace left)',
      gateReasonCode: 'revalidation-pending',
      revalidationRunsRemaining: 2,
      readyCardCount: 25,
    });

    expect(fixture.nativeElement.querySelector('[data-testid="project-pickup-blocked-banner"]')).toBeNull();
    const banner = fixture.nativeElement.querySelector(
      '[data-testid="project-pickup-revalidation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('Build profile edited: revalidation pending');
    expect(banner.textContent).toContain('2 more runs');
    fixture.destroy();
  });
});
