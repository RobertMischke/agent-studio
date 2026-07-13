import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ProjectGraphComponent, type ProjectGraphSnapshot } from './project-graph.component';

const snapshot: ProjectGraphSnapshot = {
  schemaVersion: 1,
  generatorVersion: 'project-graph-v1',
  snapshotId: 'pg-test-001',
  previousSnapshotId: null,
  captureMode: 'explicit-api',
  capturedAtUtc: '2026-07-13T12:00:00Z',
  focusProjectId: 'PROJ-002',
  focusProjectKey: 'AGT',
  projects: [{
    id: 'PROJ-002', key: 'AGT', shortCode: 'AGT', displayName: 'Agent Studio', status: 'ready', repositoryLabel: 'PROJ-002 · Agent Studio',
    sourceRevision: '0123456789abcdef', sourceState: 'clean',
    solutions: ['agent-taskboard.sln'], workflows: ['.github/workflows/backend-ci.yml'],
    technologies: [{ slug: 'dotnet', label: '.NET 10' }, { slug: 'angular', label: 'Angular 21' }, { slug: 'github-actions', label: 'GitHub Actions' }],
    componentIds: ['agt:backend', 'agt:frontend'], size: { files: 950, lines: 120_000 }, warnings: [],
  }, {
    id: 'PROJ-011', key: 'CAR', shortCode: 'CAR', displayName: 'Coding Agent Runner', status: 'ready', repositoryLabel: 'PROJ-011 · Coding Agent Runner',
    sourceRevision: null, sourceState: 'unavailable',
    solutions: ['CodingAgentRunner.slnx'], workflows: [], technologies: [{ slug: 'dotnet', label: '.NET 10' }],
    componentIds: ['car:runner'], size: { files: 120, lines: 14_000 }, warnings: [],
  }],
  components: [
    { id: 'agt:backend', projectId: 'PROJ-002', projectKey: 'AGT', name: 'OrchestratorApi', kind: 'dotnet', relativePath: 'backend/OrchestratorApi.csproj', technologies: [{ slug: 'dotnet', label: '.NET 10' }, { slug: 'aspnet-core', label: 'ASP.NET Core' }], size: { files: 520, lines: 80_000 } },
    { id: 'agt:frontend', projectId: 'PROJ-002', projectKey: 'AGT', name: 'agent-studio', kind: 'npm', relativePath: 'frontend/package.json', technologies: [{ slug: 'angular', label: 'Angular 21' }, { slug: 'typescript', label: 'TypeScript' }], size: { files: 430, lines: 40_000 } },
    { id: 'car:runner', projectId: 'PROJ-011', projectKey: 'CAR', name: 'CodingAgentRunner', kind: 'dotnet', relativePath: 'src/CodingAgentRunner/CodingAgentRunner.csproj', technologies: [{ slug: 'dotnet', label: '.NET 10' }], size: { files: 120, lines: 14_000 } },
  ],
  dependencies: [
    { fromComponentId: 'agt:backend', toComponentId: 'car:runner', kind: 'package', resolution: 'resolved', targetHint: null, evidence: 'backend/OrchestratorApi.csproj: CodingAgentRunner' },
    { fromComponentId: 'agt:frontend', toComponentId: null, kind: 'package', resolution: 'unresolved', targetHint: 'missing-ui file:<local-path>', evidence: 'frontend/package.json: missing-ui file:<local-path>' },
  ],
};

describe('ProjectGraphComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectGraphComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('loads the project catalog and renders focus metrics plus cross-project graph nodes', async () => {
    const fixture = TestBed.createComponent(ProjectGraphComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Agent%20Studio/graph').flush(snapshot);
    await fixture.whenStable();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="project-graph-component-count"]')?.textContent).toContain('2');
    expect(root.querySelectorAll('svg .project-graph__node')).toHaveLength(3);
    expect(root.textContent).toContain('cross-project');
    expect(root.textContent).toContain('CodingAgentRunner');
    expect(root.querySelector('[data-testid="project-graph-source-provenance"]')?.textContent).toContain('0123456789ab · clean');
    expect(root.textContent).toContain('unresolved local');
    http.verify();
  });

  it('switches to the complete component list without a second request', async () => {
    const fixture = TestBed.createComponent(ProjectGraphComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Agent%20Studio/graph').flush(snapshot);
    await fixture.whenStable();
    fixture.detectChanges();

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="project-graph-view-list"]')!.click();
    fixture.detectChanges();
    const rows = (fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="project-graph-component-list"] tbody tr');
    expect(rows).toHaveLength(2);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('backend/OrchestratorApi.csproj');
    const relation = (fixture.nativeElement as HTMLElement).querySelector<HTMLDetailsElement>('.project-graph__relations');
    expect(relation).not.toBeNull();
    http.verify();
  });
});
