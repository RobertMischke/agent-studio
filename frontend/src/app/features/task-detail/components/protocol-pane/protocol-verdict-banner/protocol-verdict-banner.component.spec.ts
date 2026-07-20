import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProtocolVerdictBannerComponent } from './protocol-verdict-banner.component';
import type { ProtocolVerdict } from '../protocol-verdict';
import type { VerdictChain } from '../protocol-verdict-chain';

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

async function build(v: ProtocolVerdict, chain: VerdictChain | null = null) {
  await TestBed.configureTestingModule({
    imports: [ProtocolVerdictBannerComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProtocolVerdictBannerComponent);
  fixture.componentRef.setInput('verdict', v);
  fixture.componentRef.setInput('chain', chain);
  fixture.detectChanges();
  return fixture;
}

function chain(overrides: Partial<VerdictChain> = {}): VerdictChain {
  return {
    leadingStepKey: 'lane',
    causalNarrative: 'Automated checks passed, but 1 high-severity review finding escalated this to human review.',
    steps: [
      { key: 'run', title: 'Run', status: 'ok', summary: 'Success — done.', evidence: [{ label: 'status.md', target: 'status' }] },
      { key: 'gate', title: 'Gate', status: 'ok', summary: 'No gate issue.', evidence: [] },
      { key: 'review', title: 'Review aspects', status: 'problem', summary: '1 high-severity finding.', evidence: [{ label: 'Data loss', target: 'review-evidence', ref: 'h1' }] },
      { key: 'lane', title: 'Lane decision', status: 'problem', summary: 'Escalated.', evidence: [{ label: '4-auto-review', target: 'lane', ref: '4-auto-review' }] },
    ],
    ...overrides,
  };
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

  it('keeps interim status in the running banner and allows dismissing it', async () => {
    const fixture = await build(verdict({ kind: 'unclear', label: 'Running' }));
    fixture.componentRef.setInput('running', true);
    fixture.componentRef.setInput('canRequestInterim', true);
    fixture.detectChanges();
    const html = fixture.nativeElement as HTMLElement;
    let requested = false;
    fixture.componentInstance.requestInterim.subscribe(() => (requested = true));

    html.querySelector<HTMLButtonElement>('[data-testid="protocol-interim-summary"]')!.click();
    expect(requested).toBe(true);
    html.querySelector<HTMLButtonElement>('[data-testid="protocol-running-dismiss"]')!.click();
    fixture.detectChanges();
    expect(html.querySelector('[data-testid="protocol-verdict-unclear"]')).toBeNull();
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

  describe('verdict chain (BEFUND 2 + 3)', () => {
    it('keeps the chain collapsed until expanded, then shows the four steps in order', async () => {
      const fixture = await build(verdict(), chain());
      const c = fixture.componentInstance;
      const html = fixture.nativeElement as HTMLElement;
      // A chain makes even a short-reason banner expandable.
      expect(c.expandable()).toBe(true);
      expect(html.querySelector('[data-testid="protocol-verdict-chain-steps"]')).toBeNull();

      html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-chain-toggle"]')!.click();
      fixture.detectChanges();

      const steps = html.querySelectorAll('[data-testid^="protocol-verdict-chain-step-"]');
      expect(Array.from(steps).map((s) => s.getAttribute('data-testid'))).toEqual([
        'protocol-verdict-chain-step-run',
        'protocol-verdict-chain-step-gate',
        'protocol-verdict-chain-step-review',
        'protocol-verdict-chain-step-lane',
      ]);
    });

    it('marks the leading step and renders the causal narrative (BEFUND 3)', async () => {
      const fixture = await build(verdict(), chain());
      const html = fixture.nativeElement as HTMLElement;
      html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-chain-toggle"]')!.click();
      fixture.detectChanges();

      const leading = html.querySelector('[data-testid="protocol-verdict-chain-step-lane"]');
      expect(leading!.querySelector('[data-testid="protocol-verdict-chain-leading"]')).not.toBeNull();
      expect(html.querySelector('[data-testid="protocol-verdict-chain-narrative"]')!.textContent)
        .toContain('escalated this to human review');
    });

    it('emits openEvidence with the clicked link (BEFUND 2: links to evidence)', async () => {
      const fixture = await build(verdict(), chain());
      const c = fixture.componentInstance;
      const html = fixture.nativeElement as HTMLElement;
      let emitted: unknown = null;
      c.openEvidence.subscribe((l) => (emitted = l));

      html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-chain-toggle"]')!.click();
      fixture.detectChanges();
      html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-chain-evidence-status"]')!.click();

      expect(emitted).toEqual({ label: 'status.md', target: 'status' });
    });

    it('renders no chain block when chain is null', async () => {
      const fixture = await build(verdict(), null);
      const html = fixture.nativeElement as HTMLElement;
      expect(html.querySelector('[data-testid="protocol-verdict-chain"]')).toBeNull();
    });
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
