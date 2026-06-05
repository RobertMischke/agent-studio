import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { SteeringDetailComponent } from './steering-detail.component';
import type { SteeringInfo } from './steering-detail.model';

const FULL: SteeringInfo = {
  verdict: 'reissue',
  verdictLabel: 'Re-issue',
  tone: 'warn',
  reason: 'requirement-fit not met',
  openItems: [{ aspect: 'requirement-fit', verdict: 'concerns', reason: 'missing test' }],
  prompt: 'STEER THE DIFF, DO NOT RESTART',
  context: [
    { key: 'Attempt', value: '2 / 3' },
    { key: 'Mode', value: 'resume' },
  ],
  commits: ['a1b2c3 feat: x'],
};

describe('SteeringDetailComponent', () => {
  async function mount(info: SteeringInfo, showVerdictChip?: boolean) {
    await TestBed.configureTestingModule({
      imports: [SteeringDetailComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(SteeringDetailComponent);
    fixture.componentRef.setInput('info', info);
    if (showVerdictChip !== undefined) {
      fixture.componentRef.setInput('showVerdictChip', showVerdictChip);
    }
    fixture.detectChanges();
    return fixture;
  }

  it('shows the toned verdict chip + reason head when showVerdictChip is true', async () => {
    const fixture = await mount(FULL, true);
    const el = fixture.nativeElement as HTMLElement;
    const chip = el.querySelector('[data-testid="steering-detail-verdict"]');
    expect(chip?.textContent?.trim()).toBe('Re-issue');
    expect(chip?.getAttribute('data-verdict')).toBe('reissue');
    expect(chip?.classList.contains('steer__chip--warn')).toBe(true);
    expect(el.querySelector('[data-testid="steering-detail-reason"]')?.textContent)
      .toContain('requirement-fit not met');
  });

  it('renders the reason inline with a Gap label when the chip is suppressed', async () => {
    const fixture = await mount(FULL, false);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="steering-detail-verdict"]')).toBeNull();
    const reason = el.querySelector('[data-testid="steering-detail-reason"]');
    expect(reason?.classList.contains('steer__reason--inline')).toBe(true);
    expect(reason?.textContent).toContain('Gap:');
  });

  it('labels the inline reason as Note for an accept verdict', async () => {
    const fixture = await mount(
      { ...FULL, verdict: 'accept', tone: 'ok', reason: 'looks good' },
      false,
    );
    const reason = fixture.nativeElement.querySelector('[data-testid="steering-detail-reason"]');
    expect(reason?.textContent).toContain('Note:');
  });

  it('renders open items, steer prompt, context, and commits in the collapsible body', async () => {
    const fixture = await mount(FULL, true);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="steering-detail-body"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="steering-detail-open-items"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="steering-detail-prompt"]')?.textContent)
      .toContain('STEER THE DIFF, DO NOT RESTART');
    const context = el.querySelector('[data-testid="steering-detail-context"]');
    expect(context?.textContent).toContain('Attempt');
    expect(context?.textContent).toContain('2 / 3');
    expect(el.querySelector('[data-testid="steering-detail-commits"]')?.textContent)
      .toContain('a1b2c3 feat: x');
  });

  it('renders no collapsible body when there is nothing to expand', async () => {
    const fixture = await mount({
      verdict: 'accept',
      verdictLabel: 'Accept',
      tone: 'ok',
      reason: 'all good',
      openItems: [],
      prompt: null,
      context: [],
      commits: [],
    }, true);
    expect(fixture.nativeElement.querySelector('[data-testid="steering-detail-body"]')).toBeNull();
  });

  it('renders the steer prompt verbatim without ** or [] markdown noise', async () => {
    const fixture = await mount(FULL, true);
    const pre = fixture.nativeElement.querySelector('.steer__prompt');
    expect(pre?.textContent).toBe('STEER THE DIFF, DO NOT RESTART');
  });
});
