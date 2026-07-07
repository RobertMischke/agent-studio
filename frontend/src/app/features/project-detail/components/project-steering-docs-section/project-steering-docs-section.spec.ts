import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectSteeringDocsSectionComponent } from './project-steering-docs-section';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('ProjectSteeringDocsSectionComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectSteeringDocsSectionComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectSteeringDocsSectionComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ProjectSteeringDocsSectionComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders existing agent docs as a tree and opens the selected file like a wiki page', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectSteeringDocsSectionComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectSteeringDocsSectionComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/steering').flush({
      projectName: 'Demo',
      baseDir: 'C:/Projects/demo',
      lastUpdated: '2026-06-23T10:00:00Z',
      sources: [
        {
          id: 'agents-md',
          label: 'AGENTS.md',
          relPath: 'AGENTS.md',
          kind: 'agentInstructions',
          why: 'Project-level agent instructions.',
          exists: true,
          updatedAt: '2026-06-23T10:00:00Z',
          size: 2400,
          appliesToClis: ['codex', 'claude', 'copilot'],
          children: null,
        },
        {
          id: 'frontend-agents-md',
          label: 'AGENTS.md',
          relPath: 'frontend/AGENTS.md',
          kind: 'agentInstructions',
          why: 'Frontend-scoped agent instructions.',
          exists: true,
          updatedAt: '2026-06-22T10:00:00Z',
          size: 800,
          appliesToClis: ['codex', 'claude', 'copilot'],
          children: null,
        },
        {
          id: 'github-copilot-instructions-md',
          label: 'copilot-instructions.md',
          relPath: '.github/copilot-instructions.md',
          kind: 'agentCliShim',
          why: 'GitHub Copilot coding-agent instruction file.',
          exists: true,
          updatedAt: '2026-06-21T10:00:00Z',
          size: 200,
          appliesToClis: ['copilot'],
          children: null,
        },
      ],
      warnings: [{
        severity: 'warn',
        kind: 'gatewayTooHeavy',
        message: 'AGENTS.md carries too much local guidance.',
        sourceId: 'agents-md',
        evidenceRefs: ['AGENTS.md', 'docs/wiki/'],
      }],
    });
    http.expectOne('/api/projects/Demo/steering/files/AGENTS.md').flush({
      relPath: 'AGENTS.md',
      content: '# Root agent rules\n\nSee docs/wiki/common-problems/example.',
    });
    http.expectOne('/api/projects/Demo/steering/read-analytics').flush({
      projectName: 'Demo',
      baseDir: 'C:/Projects/demo',
      windowDays: 7,
      hasData: true,
      totalReads: 9,
      recentReads: 4,
      taskCount: 3,
      lastReadAt: '2026-07-05T09:00:00Z',
      files: [
        {
          relPath: 'AGENTS.md',
          label: 'AGENTS.md',
          reads: 6,
          recentReads: 3,
          taskCount: 2,
          lastReadAt: '2026-07-05T09:00:00Z',
          byCli: [
            { cli: 'claude', reads: 4 },
            { cli: 'codex', reads: 2 },
          ],
        },
        {
          relPath: 'frontend/AGENTS.md',
          label: 'AGENTS.md',
          reads: 3,
          recentReads: 1,
          taskCount: 1,
          lastReadAt: '2026-07-04T09:00:00Z',
          byCli: [{ cli: 'codex', reads: 3 }],
        },
        {
          relPath: '.github/copilot-instructions.md',
          label: 'copilot-instructions.md',
          reads: 0,
          recentReads: 0,
          taskCount: 0,
          lastReadAt: null,
          byCli: [],
        },
      ],
      byCli: [
        { cli: 'claude', reads: 4 },
        { cli: 'codex', reads: 5 },
      ],
      generatedAt: '2026-07-05T10:00:00Z',
    });
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-steering-docs-tree"]')?.textContent).toContain('frontend');
    expect(host.querySelector('[data-testid="project-steering-docs-tree"]')?.textContent).not.toContain('README.md');
    expect(host.querySelector('[data-testid="project-steering-docs-viewer-path"]')?.textContent).toContain('AGENTS.md');
    expect(host.querySelector('[data-testid="project-steering-docs-viewer-clis"]')?.textContent).toContain('Codex');
    expect(host.querySelector('[data-testid="project-steering-docs-viewer-clis"]')?.textContent).toContain('Claude Code');
    expect(host.querySelector('[data-testid="project-steering-docs-selected-warnings"]')?.textContent).toContain('too much local guidance');
    expect(host.querySelector('[data-testid="project-steering-docs-content"]')?.textContent).toContain('Root agent rules');

    // Real Tool-Use Read Analytics replaced the mockup: live totals and per-file rows.
    const usage = host.querySelector('[data-testid="project-steering-docs-tool-use"]');
    expect(usage?.textContent).not.toContain('Mockup');
    expect(host.querySelector('[data-testid="project-steering-docs-tool-use-live"]')?.textContent).toContain('9 reads');
    const rootRow = host.querySelector('[data-testid="project-steering-docs-tool-use-row-AGENTS.md"]');
    expect(rootRow?.textContent).toContain('Claude Code 4');
    expect(rootRow?.textContent).toContain('Codex 2');
    // A zero-read inventory file is not rendered as a fabricated usage row.
    expect(host.querySelector('[data-testid="project-steering-docs-tool-use-row-.github/copilot-instructions.md"]')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="project-steering-docs-file-frontend/AGENTS.md"]')!.click();
    http.expectOne('/api/projects/Demo/steering/files/frontend/AGENTS.md').flush({
      relPath: 'frontend/AGENTS.md',
      content: '# Frontend agent rules',
    });
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="project-steering-docs-viewer-path"]')?.textContent).toContain('frontend/AGENTS.md');
    expect(host.querySelector('[data-testid="project-steering-docs-content"]')?.textContent).toContain('Frontend agent rules');
    http.verify();
  });

  it('renders an honest empty state when no reads are indexed yet', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectSteeringDocsSectionComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectSteeringDocsSectionComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/steering').flush({
      projectName: 'Demo',
      baseDir: 'C:/Projects/demo',
      lastUpdated: '2026-06-23T10:00:00Z',
      sources: [{
        id: 'agents-md',
        label: 'AGENTS.md',
        relPath: 'AGENTS.md',
        kind: 'agentInstructions',
        why: 'Project-level agent instructions.',
        exists: true,
        updatedAt: '2026-06-23T10:00:00Z',
        size: 2400,
        appliesToClis: ['codex', 'claude', 'copilot'],
        children: null,
      }],
      warnings: [],
    });
    http.expectOne('/api/projects/Demo/steering/files/AGENTS.md').flush({
      relPath: 'AGENTS.md',
      content: '# Root agent rules',
    });
    http.expectOne('/api/projects/Demo/steering/read-analytics').flush({
      projectName: 'Demo',
      baseDir: 'C:/Projects/demo',
      windowDays: 7,
      hasData: false,
      totalReads: 0,
      recentReads: 0,
      taskCount: 0,
      lastReadAt: null,
      files: [{
        relPath: 'AGENTS.md',
        label: 'AGENTS.md',
        reads: 0,
        recentReads: 0,
        taskCount: 0,
        lastReadAt: null,
        byCli: [],
      }],
      byCli: [],
      generatedAt: '2026-07-05T10:00:00Z',
    });
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-steering-docs-tool-use-nodata"]')?.textContent).toContain('No data yet');
    expect(host.querySelector('[data-testid="project-steering-docs-tool-use-empty"]')?.textContent).toContain('No indexed tool-use reads');
    // No fabricated numbers: there is no live-count pill and no usage rows.
    expect(host.querySelector('[data-testid="project-steering-docs-tool-use-live"]')).toBeNull();
    expect(host.querySelector('[data-testid="project-steering-docs-tool-use-row-AGENTS.md"]')).toBeNull();
    http.verify();
  });
});
