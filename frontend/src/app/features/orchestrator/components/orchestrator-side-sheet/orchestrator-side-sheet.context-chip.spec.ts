import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

/**
 * F14 unit coverage for the context-chip computed signals and caching
 * state. Exercises the parts that the e2e suite cannot reach without a
 * full host-app stub (host-driven `activeJobId` input + cache reset on
 * project change).
 */
describe('OrchestratorSideSheetComponent · context chip', () => {
  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    return fixture;
  }

  it('renders "Context: <project> · Board" when no task is open', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    c.activeProject.set('demo-project');
    expect(c.contextChipText()).toBe('Context: demo-project · Board');
    expect(c.contextChipVisible()).toBe(true);
  });

  it('renders "Context: <project> · Task \'<title>\'" when a task is in scope', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('activeJobId', 'bug-foo');
    fixture.componentRef.setInput('activeJobTitle', 'Bug: foo broke');
    const c = fixture.componentInstance;
    c.activeProject.set('demo-project');
    expect(c.contextChipText()).toBe(`Context: demo-project · Task 'Bug: foo broke'`);
  });

  it('returns null and hides the chip when no project is active', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    c.activeProject.set(null);
    expect(c.contextChipText()).toBeNull();
    expect(c.contextChipVisible()).toBe(false);
  });

  it('dismissContextChip hides the chip for the current picker state', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    c.activeProject.set('demo-project');
    expect(c.contextChipVisible()).toBe(true);
    c.dismissContextChip();
    expect(c.contextChipVisible()).toBe(false);
  });

  it('subtitle tracks the active project (F14 sticky-subtitle bug fix)', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    c.activeProject.set('demo-project');
    expect(c.subtitleText()).toBe('demo-project · canonical session');
    c.activeProject.set('other-project');
    expect(c.subtitleText()).toBe('other-project · canonical session');
  });
});
