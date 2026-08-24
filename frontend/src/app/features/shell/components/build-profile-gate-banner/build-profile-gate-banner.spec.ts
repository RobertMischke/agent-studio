import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { BuildProfileGateBannerComponent } from './build-profile-gate-banner';

const QUIET_SNAPSHOT = {
  active: false,
  waitingTaskCount: 0,
  availableSlots: 4,
  thresholdMinutes: 30,
  claimProgressStalled: false,
  lastSuccessfulClaimAt: '2026-08-23T09:59:00Z',
  hasRejections: false,
  oldestEnteredLaneAt: null,
  observedAt: '2026-08-23T10:00:00Z',
  items: [],
  gateBlockedTaskCount: 0,
  gateBlockedProjects: [],
};

function mount(projects: readonly string[]) {
  const fixture = TestBed.createComponent(BuildProfileGateBannerComponent);
  fixture.componentRef.setInput('projects', projects);
  fixture.detectChanges();
  return fixture;
}

function banner(fixture: ReturnType<typeof mount>): HTMLElement | null {
  return fixture.nativeElement.querySelector('[data-testid="build-profile-gate-banner"]');
}

describe('BuildProfileGateBannerComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BuildProfileGateBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('names the project, the held card count, and the gate reason', async () => {
    const fixture = mount(['QualityStudio']);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      ...QUIET_SNAPSHOT,
      active: true,
      waitingTaskCount: 25,
      gateBlockedTaskCount: 25,
      gateBlockedProjects: [{
        projectName: 'QualityStudio',
        readyTaskCount: 25,
        gateCode: 'not-validated',
        gateReason: 'build profile declared but not yet validated (no green dry-run and no green run on the assigned runner)',
        buildProfileStatus: 'declared',
      }],
    });
    fixture.detectChanges();

    const element = banner(fixture)!;
    expect(element.textContent).toContain('25 ready cards are not claimable: build profile not validated');
    expect(element.textContent).toContain('QualityStudio (25)');
    expect(element.textContent).toContain('no green dry-run');
    expect(element.textContent).toContain('Re-run the build-profile validation');
    fixture.destroy();
    http.verify();
  });

  it('stays silent when no project has a closed gate', async () => {
    const fixture = mount(['QualityStudio']);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush(QUIET_SNAPSHOT);
    fixture.detectChanges();

    expect(banner(fixture)).toBeNull();
    fixture.destroy();
    http.verify();
  });

  it('ignores gated projects the operator is not looking at', async () => {
    const fixture = mount(['Demo']);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      ...QUIET_SNAPSHOT,
      active: true,
      gateBlockedTaskCount: 25,
      gateBlockedProjects: [{
        projectName: 'QualityStudio',
        readyTaskCount: 25,
        gateCode: 'not-validated',
        gateReason: 'build profile declared but not yet validated',
        buildProfileStatus: 'declared',
      }],
    });
    fixture.detectChanges();

    expect(banner(fixture)).toBeNull();
    fixture.destroy();
    http.verify();
  });

  it('summarises several gated projects without claiming one shared reason', async () => {
    const fixture = mount([]);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      ...QUIET_SNAPSHOT,
      active: true,
      gateBlockedTaskCount: 7,
      gateBlockedProjects: [
        {
          projectName: 'QualityStudio',
          readyTaskCount: 5,
          gateCode: 'not-validated',
          gateReason: 'build profile declared but not yet validated',
          buildProfileStatus: 'declared',
        },
        {
          projectName: 'Demo',
          readyTaskCount: 2,
          gateCode: 'validation-failed',
          gateReason: 'last validation dry-run failed: build exited 1',
          buildProfileStatus: 'validation-failed',
        },
      ],
    });
    fixture.detectChanges();

    const element = banner(fixture)!;
    expect(element.textContent).toContain('7 ready cards are not claimable');
    expect(element.textContent).toContain('QualityStudio (5), Demo (2)');
    expect(element.textContent).not.toContain('build exited 1');
    fixture.destroy();
    http.verify();
  });
});
