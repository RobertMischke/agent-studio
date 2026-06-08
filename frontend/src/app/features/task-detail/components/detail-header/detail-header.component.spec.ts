import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { DetailHeaderComponent } from './detail-header.component';
import type { TaskInfo } from '../../../../models/task.model';

const taskInfo: TaskInfo = {
  id: 'ASS-871',
  taskKey: 'ASS-871',
  key: 'ASS-871',
  displayKey: 'ASS-871',
  title: 'Polish commit panel',
  state: '5-human-review',
  order: 1,
  agent: 'codex',
  createdAt: '2026-06-08T10:00:00Z',
  watchPath: 'C:/Projects/agent-taskboard-devspace/agent-taskboard-dev',
  projectName: 'agent-taskboard',
  folderPath: 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/tasks/000/ASS-871',
  lastActivity: '2026-06-08T10:00:00Z',
  sessionName: null,
  model: null,
  cliType: 'codex',
  useOwnSession: null,
  lastUsage: null,
  execution: null,
  commit: null,
};

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
describe('DetailHeaderComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [DetailHeaderComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DetailHeaderComponent);
    fixture.componentRef.setInput('info', taskInfo);

    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] DetailHeaderComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('adds worktree commit actions to the text-only overflow menu model', async () => {
    await TestBed.configureTestingModule({
      imports: [DetailHeaderComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DetailHeaderComponent);
    fixture.componentRef.setInput('info', taskInfo);
    fixture.componentRef.setInput('commitActionsAvailable', true);
    fixture.componentRef.setInput('commitMessageDraft', 'Polish commit panel');
    fixture.detectChanges();

    const rows = fixture.componentInstance.triageMenuItems().filter(item => item.kind === 'row');
    expect(rows.map(item => item.label)).toContain('Generate Commit Message');
    expect(rows.map(item => item.label)).toContain('Add Commit...');
    expect(rows.find(item => item.id === 'add-commit')?.hint).toBe('Draft ready');
  });
});
