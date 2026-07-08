import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProtocolVerdictBannerComponent } from './protocol-verdict-banner.component';
import type { ProtocolVerdict } from '../protocol-verdict';

function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    duration: null,
    superseded: null,
    ...overrides,
  };
}

async function build(v: ProtocolVerdict) {
  await TestBed.configureTestingModule({
    imports: [ProtocolVerdictBannerComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProtocolVerdictBannerComponent);
  fixture.componentRef.setInput('verdict', v);
  fixture.detectChanges();
  return fixture;
}

const LONG_REASON =
  'Rewrote protocol-verdict.ts rendering all five canonical states, but could not verify the banner end to end.';

describe('ProtocolVerdictBannerComponent', () => {
  it('renders a short reason as a plain, non-expandable line', async () => {
    const fixture = await build(verdict());
    const html = fixture.nativeElement as HTMLElement;
    expect(fixture.componentInstance.expandable()).toBe(false);
    const detail = html.querySelector('[data-testid="protocol-verdict-detail"]');
    expect(detail).not.toBeNull();
    expect(detail!.tagName).toBe('SPAN');
    expect(detail!.textContent).toContain('completed successfully');
  });

  it('offers an expand toggle for a long reason and unclamps on click (BEFUND 1)', async () => {
    const fixture = await build(verdict({ kind: 'problem', emoji: '🔴', label: 'Blocked', detail: LONG_REASON }));
    const c = fixture.componentInstance;
    const html = fixture.nativeElement as HTMLElement;
    expect(c.expandable()).toBe(true);
    const btn = html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-detail"]');
    expect(btn?.tagName).toBe('BUTTON');
    expect(html.querySelector('.protocol-verdict--expanded')).toBeNull();

    btn!.click();
    fixture.detectChanges();
    expect(c.expanded()).toBe(true);
    expect(html.querySelector('.protocol-verdict--expanded')).not.toBeNull();
    // The whole reason is present in the DOM (clamped only visually).
    expect(html.querySelector('[data-testid="protocol-verdict-detail"]')!.textContent)
      .toContain('could not verify the banner end to end');
  });

  it('renders a superseded blocker as collapsed history, never as the head banner (BEFUND 2/3)', async () => {
    const fixture = await build(
      verdict({
        kind: 'ok',
        label: 'Accepted',
        detail: 'Current stand: accepted by review. An earlier run reported a blocker, kept as history below.',
        superseded: { label: 'Blocked', detail: 'sandbox denied write to /etc' },
      }),
    );
    const html = fixture.nativeElement as HTMLElement;
    // Head banner leads with the accepted stand.
    expect(html.querySelector('[data-testid="protocol-verdict-ok"]')).not.toBeNull();
    // Superseded strip is present but its detail is hidden until expanded.
    const strip = html.querySelector('[data-testid="protocol-verdict-superseded"]');
    expect(strip).not.toBeNull();
    expect(strip!.textContent).toContain('Superseded run outcome: Blocked');
    expect(html.querySelector('[data-testid="protocol-verdict-superseded-detail"]')).toBeNull();

    html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-superseded-toggle"]')!.click();
    fixture.detectChanges();
    expect(html.querySelector('[data-testid="protocol-verdict-superseded-detail"]')?.textContent)
      .toContain('sandbox denied write to /etc');
  });

  it('collapses again when the verdict content changes', async () => {
    const fixture = await build(verdict({ label: 'Blocked', kind: 'problem', detail: LONG_REASON }));
    const c = fixture.componentInstance;
    c.toggle();
    fixture.detectChanges();
    expect(c.expanded()).toBe(true);

    fixture.componentRef.setInput('verdict', verdict({ label: 'Done', kind: 'ok', detail: LONG_REASON + ' extra' }));
    fixture.detectChanges();
    expect(c.expanded()).toBe(false);
  });
});
