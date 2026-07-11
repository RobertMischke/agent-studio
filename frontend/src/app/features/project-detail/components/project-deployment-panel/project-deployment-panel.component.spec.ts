import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProjectDeploymentPanelComponent } from './project-deployment-panel.component';

describe('ProjectDeploymentPanelComponent', () => {
  it('renders the shared baseline, pending delta, and newest-first history', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/projects/Demo%20Project/deployment/summary')
      .flush({
        project: 'Demo Project', available: true, reason: null,
        source: 'logs/stable-restarts.jsonl', pendingCount: 2,
        pendingCommits: [{ sha: 'cccccccc', shortSha: 'cccccccc', subject: 'Pending', authorDateUtc: '2026-07-11T10:00:00Z' }],
        lastDeployment: { at: '2026-07-11T09:00:00Z', status: 'ok', headBefore: 'aaaaaaaa', headAfter: 'bbbbbbbb', durationSeconds: 42, jobsSinceLastRestart: 3, reviewCountAfter: 4, commits: [] },
        history: [
          { at: '2026-07-11T09:00:00Z', status: 'ok', headBefore: 'aaaaaaaa', headAfter: 'bbbbbbbb', durationSeconds: 42, jobsSinceLastRestart: 3, reviewCountAfter: 4, commits: [] },
          { at: '2026-07-10T09:00:00Z', status: 'failed', headBefore: '11111111', headAfter: '22222222', durationSeconds: 7, jobsSinceLastRestart: 1, reviewCountAfter: 2, commits: [] },
        ],
      });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-pending-count"]').textContent.trim()).toBe('2');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="project-deployment-history"] > li').length).toBe(2);
    expect(fixture.nativeElement.textContent).toContain('deploy-stable');
    expect(fixture.nativeElement.textContent).not.toContain('Run deployment');
  });

  it('explains project-specific unavailable history', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Other');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Other/deployment/summary').flush({
      project: 'Other', available: false, reason: 'Latest deploy-stable revision range does not belong to this project repository.',
      source: 'logs/stable-restarts.jsonl', lastDeployment: null, history: [], pendingCount: null, pendingCommits: [],
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-unavailable"]').textContent)
      .toContain('does not belong to this project repository');
  });
});
