import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, describe, expect, it } from 'vitest';
import { OverviewFailureComponent } from './overview-failure.component';

afterEach(() => TestBed.resetTestingModule());

describe('OverviewFailureComponent', () => {
  it('keeps raw diagnostics collapsed while exposing a complete human sentence', async () => {
    await TestBed.configureTestingModule({
      imports: [OverviewFailureComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const raw = '[orchestrator] [watchdog-timeout] auto-cancelled after 601s of silence. [phase=TurnCompleted silence=601s allowed=600s complete-tail]';
    const fixture = TestBed.createComponent(OverviewFailureComponent);
    fixture.componentRef.setInput('issue', {
      kind: 'watchdog-timeout', label: 'Watchdog timeout', severity: 'High', summary: 'Truncated...',
      technicalDetails: raw, lastSeenAt: '2026-07-22T09:20:01.000Z',
    });
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const primary = host.querySelector('[data-testid="overview-failure-primary"]');
    const details = host.querySelector('[data-testid="overview-failure-details"]') as HTMLDetailsElement;
    expect(primary?.textContent?.trim()).toBe('Run automatically stopped after 10 minutes without progress (watchdog).');
    expect(primary?.textContent).not.toContain('phase=');
    expect(details.open).toBe(false);
    expect(host.querySelector('[data-testid="overview-failure-raw"]')?.textContent).toBe(raw);

    details.querySelector('summary')?.click();
    expect(details.open).toBe(true);
  });
});
