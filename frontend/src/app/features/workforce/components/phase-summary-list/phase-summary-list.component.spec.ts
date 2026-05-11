import { describe, it, expect, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PhaseSummaryListComponent } from './phase-summary-list.component';
import { groupIntoPhases, type PhaseInputMessage } from '../../models/chat-phase';

function fixturePhases() {
  const messages: PhaseInputMessage[] = [
    { id: 'a', ts: '2026-05-11T10:00:00Z', author: 'user' },
    { id: 'b', ts: '2026-05-11T10:01:00Z', author: 'claude' },
    { id: 'c', ts: '2026-05-11T10:02:00Z', author: 'claude', refs: ['aspect:code-quality'] },
    { id: 'd', ts: '2026-05-11T10:10:00Z', author: 'user' },
    { id: 'e', ts: '2026-05-11T10:11:00Z', author: 'codex' },
  ];
  return groupIntoPhases(messages);
}

describe('PhaseSummaryListComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('defaults to expanding only the newest phase', () => {
    const fixture = TestBed.createComponent(PhaseSummaryListComponent);
    fixture.componentRef.setInput('phases', fixturePhases());
    fixture.detectChanges();
    const rows = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('[data-testid="phase-summary-row"]')
    );
    expect(rows.length).toBe(2);
    expect(rows[0].getAttribute('data-expanded')).toBe('false');
    expect(rows[1].getAttribute('data-expanded')).toBe('true');
  });

  it('toggles a phase open/closed when its row is clicked', () => {
    const fixture = TestBed.createComponent(PhaseSummaryListComponent);
    fixture.componentRef.setInput('phases', fixturePhases());
    fixture.detectChanges();
    const firstToggle = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-testid="phase-summary-toggle-phase-a"]'
    );
    expect(firstToggle).toBeTruthy();
    firstToggle!.click();
    fixture.detectChanges();
    const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-phase-id="phase-a"]'
    );
    expect(row?.getAttribute('data-expanded')).toBe('true');
  });

  it('emits phaseToggled with the new expansion state', () => {
    const fixture = TestBed.createComponent(PhaseSummaryListComponent);
    fixture.componentRef.setInput('phases', fixturePhases());
    fixture.detectChanges();
    const events: { phaseId: string; expanded: boolean }[] = [];
    fixture.componentInstance.phaseToggled.subscribe((e) => events.push(e));
    const toggle = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-testid="phase-summary-toggle-phase-a"]'
    );
    toggle!.click();
    fixture.detectChanges();
    expect(events).toEqual([{ phaseId: 'phase-a', expanded: true }]);
  });

  it('renders the deterministic summary line for every phase', () => {
    const fixture = TestBed.createComponent(PhaseSummaryListComponent);
    fixture.componentRef.setInput('phases', fixturePhases());
    fixture.detectChanges();
    const lines = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.phase-summary__line')
    ).map((el) => el.textContent ?? '');
    expect(lines[0]).toContain('You steered');
    expect(lines[0]).toContain('Task Executor');
    expect(lines[1]).toContain('Task Executor');
  });
});
