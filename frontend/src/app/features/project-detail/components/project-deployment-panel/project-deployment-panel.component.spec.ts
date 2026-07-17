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
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/watch-paths').flush([{ name: 'Demo Project', path: 'C:/projects/demo' }]);
    http
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
        targets: [{ id: 'deploy-stable', title: 'deploy-stable', kind: 'derived', template: 'deploy-stable', summary: 'Deploy stable.', runnable: true, source: 'repository-fact', command: 'bash scripts/supervisor/restart-stable-after-batch.sh', targetHostId: null, parameters: [{ name: 'stableIdle', type: 'boolean', required: true, default: false, options: [] }] }],
      });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-pending-count"]').textContent.trim()).toBe('2');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="project-deployment-history"] > li').length).toBe(2);
    expect(fixture.nativeElement.textContent).toContain('deploy-stable');
    expect(fixture.nativeElement.textContent).not.toContain('Run deployment');

    fixture.nativeElement.querySelector('[data-testid="deployment-visible-task"]').click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Visible task execution is required for this workflow.');
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-run"]').getAttribute('aria-describedby'))
      .toBe('deployment-visible-task-required');
    fixture.nativeElement.querySelector('[data-testid="deployment-visible-task"]').click();
    fixture.nativeElement.querySelector('[data-testid="deployment-param-stableIdle"]').click();
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="project-deployment-run"]').click();
    const create = http.expectOne('/api/tasks');
    expect(create.request.body.promptMarkdown).toContain('deploymentTarget: deploy-stable');
    create.flush({ id: 'AGT-3000' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-created"]').textContent).toContain('AGT-3000');
  });

  it('accepts false as a provided value for ordinary required yes-no parameters', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    const target = {
      id: 'docs', title: 'Docs', kind: 'template' as const, template: 'caddy-site', summary: 'Deploy docs.',
      runnable: true, source: 'deployment.json', command: 'bash scripts/deploy.sh --reload {{reload}}', targetHostId: 'web',
      parameters: [{ name: 'reload', type: 'boolean' as const, required: true, default: false, options: [] }],
    };

    fixture.componentInstance.chooseTarget(target);

    expect(fixture.componentInstance.canRun(target)).toBe(true);
  });

  it('explains project-specific unavailable history', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Other');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/watch-paths').flush([]);
    http.expectOne('/api/projects/Other/deployment/summary').flush({
      project: 'Other', available: false, reason: 'Latest deploy-stable revision range does not belong to this project repository.',
      source: 'logs/stable-restarts.jsonl', lastDeployment: null, history: [], pendingCount: null, pendingCommits: [], targets: [],
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-unavailable"]').textContent)
      .toContain('does not belong to this project repository');
  });
});
