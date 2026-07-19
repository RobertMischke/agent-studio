import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliWorkingMemoryPanelComponent } from './cli-working-memory-panel';
import type {
  CliType,
} from '../../../../models/task.model';
import type {
  CliWorkingMemoryEntry,
  CliWorkingMemoryReport,
  CliWorkingMemoryDeleteResult,
} from '../../../../features/cli';

function entry(partial: Partial<CliWorkingMemoryEntry>): CliWorkingMemoryEntry {
  return {
    id: partial.path ?? 'id',
    cliType: 'claude',
    kind: 'memory',
    label: 'User memory',
    path: '/home/.claude/CLAUDE.md',
    isDirectory: false,
    sizeBytes: 128,
    itemCount: null,
    lastModifiedUtc: '2026-06-10T12:00:00Z',
    preview: 'remember to be terse',
    deletable: true,
    detail: null,
    ...partial,
  };
}

function report(cli: CliType, entries: CliWorkingMemoryEntry[]): CliWorkingMemoryReport {
  return {
    cliType: cli,
    available: true,
    root: `/home/.${cli}`,
    capturedAt: '2026-06-11T00:00:00Z',
    entries,
  };
}

const MEMORY = entry({ path: '/home/.claude/CLAUDE.md', kind: 'memory', deletable: true });
const AUTH = entry({
  path: '/home/.claude/.credentials.json',
  kind: 'auth',
  label: 'OAuth credentials',
  deletable: false,
  preview: null,
  detail: 'Authentication / credentials are never deleted here.',
});

async function setup() {
  await TestBed.configureTestingModule({
    imports: [CliWorkingMemoryPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  }).compileComponents();

  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(CliWorkingMemoryPanelComponent);
  fixture.detectChanges();

  // One GET per CLI; claude carries a deletable memory + a protected auth row.
  http.expectOne((r) => r.url.endsWith('/cli/claude/working-memory')).flush(report('claude', [MEMORY, AUTH]));
  http.expectOne((r) => r.url.endsWith('/cli/codex/working-memory')).flush(report('codex', []));
  http.expectOne((r) => r.url.endsWith('/cli/gemini/working-memory')).flush(report('gemini', []));

  return { http, fixture, component: fixture.componentInstance };
}

describe('CliWorkingMemoryPanelComponent', () => {
  it('loads a per-CLI working-memory report for every CLI on init', async () => {
    const { http, component } = await setup();
    expect(component.reportFor('claude')?.entries.length).toBe(2);
    expect(component.reportFor('codex')?.entries.length).toBe(0);
    http.verify();
  });

  it('marks the auth entry as protected (not deletable, no preview)', async () => {
    const { http, component } = await setup();
    const auth = component.reportFor('claude')!.entries.find((e) => e.kind === 'auth')!;
    expect(auth.deletable).toBe(false);
    expect(auth.preview).toBeNull();
    http.verify();
  });

  it('deletes a memory entry through a two-step confirm and refreshes the report', async () => {
    const { http, component } = await setup();

    // Arm the inline confirm, then confirm.
    component.requestDelete(MEMORY);
    expect(component.isPending(MEMORY)).toBe(true);

    component.confirmDelete('claude', MEMORY);

    const del = http.expectOne(
      (r) => r.method === 'DELETE' && r.url.endsWith('/cli/claude/working-memory'),
    );
    expect(del.request.params.get('path')).toBe(MEMORY.path);

    const result: CliWorkingMemoryDeleteResult = {
      status: 'Deleted',
      message: "Deleted 'User memory'.",
      freedBytes: 128,
      report: report('claude', [AUTH]), // memory gone, auth remains
    };
    del.flush(result);

    expect(component.reportFor('claude')?.entries.some((e) => e.kind === 'memory')).toBe(false);
    expect(component.reportFor('claude')?.entries.some((e) => e.kind === 'auth')).toBe(true);
    expect(component.notice()).toContain('Deleted');
    expect(component.isPending(MEMORY)).toBe(false);
    http.verify();
  });

  it('surfaces a server refusal as an error and keeps the entry', async () => {
    const { http, component } = await setup();

    component.confirmDelete('claude', AUTH); // defensive: UI never offers this

    const del = http.expectOne(
      (r) => r.method === 'DELETE' && r.url.endsWith('/cli/claude/working-memory'),
    );
    const refused: CliWorkingMemoryDeleteResult = {
      status: 'Protected',
      message: "'OAuth credentials' is protected (auth) and is never deleted.",
      freedBytes: 0,
      report: report('claude', [MEMORY, AUTH]),
    };
    del.flush(refused);

    expect(component.errorMsg()).toContain('protected');
    expect(component.reportFor('claude')?.entries.some((e) => e.kind === 'auth')).toBe(true);
    http.verify();
  });
});
