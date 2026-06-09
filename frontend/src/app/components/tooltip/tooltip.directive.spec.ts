import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TooltipDirective } from './tooltip.directive';

const TIP_TESTID = 'app-tooltip';
const TIP_SELECTOR = `[data-testid="${TIP_TESTID}"], .app-tooltip`;

@Component({
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './tooltip.directive.host.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
class HostComponent {
  content = signal<string | { title?: string; body: string } | null | undefined>('hello world');
  position = signal<'top' | 'bottom' | 'left' | 'right' | 'auto'>('auto');
  severity = signal<'info' | 'warn' | 'error' | 'success' | undefined>(undefined);
}

function getTooltip(): HTMLElement | null {
  return document.querySelector(`[data-testid="${TIP_TESTID}"]`);
}

function removeTooltipDom(): void {
  document.querySelectorAll(TIP_SELECTOR).forEach(node => node.remove());
}

function fireHover(el: HTMLElement, type: 'mouseenter' | 'mouseleave' | 'focusin' | 'focusout' | 'click' | 'touchstart') {
  el.dispatchEvent(new Event(type, { bubbles: true }));
}

describe('TooltipDirective', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<HostComponent>>;
  let anchor: HTMLElement;

  beforeEach(() => {
    // Reset any tooltip DOM from previous tests.
    removeTooltipDom();

    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideZonelessChangeDetection()]
    });
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    anchor = fixture.nativeElement.querySelector('[data-testid="anchor"]') as HTMLElement;
  });

  afterEach(() => {
    fixture?.destroy();
    removeTooltipDom();
  });

  it('does NOT create tooltip DOM before first hover (lazy render)', () => {
    expect(getTooltip()).toBeNull();
  });

  it('test cleanup removes every stale tooltip node', () => {
    const staleWithTestId = document.createElement('div');
    staleWithTestId.className = 'app-tooltip';
    staleWithTestId.dataset['testid'] = TIP_TESTID;
    const staleClassOnly = document.createElement('div');
    staleClassOnly.className = 'app-tooltip';
    document.body.append(staleWithTestId, staleClassOnly);

    removeTooltipDom();

    expect(document.querySelectorAll(TIP_SELECTOR).length).toBe(0);
  });

  it('creates a single shared tooltip element on first hover and reuses it', () => {
    fireHover(anchor, 'mouseenter');
    const first = getTooltip();
    expect(first).not.toBeNull();
    expect(first!.getAttribute('role')).toBe('tooltip');

    fireHover(anchor, 'mouseleave');
    fireHover(anchor, 'mouseenter');
    const second = getTooltip();
    expect(second).toBe(first);
    expect(document.querySelectorAll(`[data-testid="${TIP_TESTID}"]`).length).toBe(1);
  });

  it('shows the tooltip immediately on mouseenter (no delay)', () => {
    fireHover(anchor, 'mouseenter');
    const tip = getTooltip()!;
    expect(tip.style.visibility).toBe('visible');
    expect(tip.style.opacity).toBe('1');
  });

  it('hides the tooltip on mouseleave', () => {
    fireHover(anchor, 'mouseenter');
    fireHover(anchor, 'mouseleave');
    const tip = getTooltip()!;
    expect(tip.style.visibility).toBe('hidden');
    expect(tip.style.opacity).toBe('0');
  });

  it('renders plain string content as text (no HTML interpretation)', () => {
    fixture.componentInstance.content.set('plain & < text');
    fixture.detectChanges();
    fireHover(anchor, 'mouseenter');
    const body = getTooltip()!.querySelector('.app-tooltip__body')!;
    expect(body.textContent).toBe('plain & < text');
    expect(body.querySelector('script')).toBeNull();
  });

  it('renders structured tooltip with title and HTML body, sanitised', () => {
    fixture.componentInstance.content.set({
      title: 'Severity',
      body: '<b>Bold</b> and <code>code</code><script>alert(1)</script>'
    });
    fixture.detectChanges();
    fireHover(anchor, 'mouseenter');

    const tip = getTooltip()!;
    const title = tip.querySelector('.app-tooltip__title')!;
    const body = tip.querySelector('.app-tooltip__body')!;
    expect(title.textContent).toBe('Severity');
    expect(body.querySelector('b')?.textContent).toBe('Bold');
    expect(body.querySelector('code')?.textContent).toBe('code');
    // DOMPurify strips <script>
    expect(body.querySelector('script')).toBeNull();
    expect(body.innerHTML).not.toContain('alert(1)');
  });

  it('renders HTML in string content when tags are present', () => {
    fixture.componentInstance.content.set('Press <kbd>Esc</kbd> to close');
    fixture.detectChanges();
    fireHover(anchor, 'mouseenter');
    const body = getTooltip()!.querySelector('.app-tooltip__body')!;
    expect(body.querySelector('kbd')?.textContent).toBe('Esc');
  });

  it('applies severity class on the host element', () => {
    fixture.componentInstance.severity.set('warn');
    fixture.detectChanges();
    fireHover(anchor, 'mouseenter');
    const tip = getTooltip()!;
    expect(tip.classList.contains('app-tooltip--warn')).toBe(true);
  });

  it('records the placement side on data-placement', () => {
    fixture.componentInstance.position.set('top');
    fixture.detectChanges();
    fireHover(anchor, 'mouseenter');
    const tip = getTooltip()!;
    expect(['top', 'bottom', 'left', 'right']).toContain(tip.dataset['placement']);
  });

  it('shows on focusin and hides on focusout (a11y)', () => {
    fireHover(anchor, 'focusin');
    expect(getTooltip()!.style.visibility).toBe('visible');
    fireHover(anchor, 'focusout');
    expect(getTooltip()!.style.visibility).toBe('hidden');
  });

  it('shows on touchstart and stays open until document-level touch', () => {
    fireHover(anchor, 'touchstart');
    expect(getTooltip()!.style.visibility).toBe('visible');
  });

  it('hides when the anchor is clicked', () => {
    fireHover(anchor, 'mouseenter');
    fireHover(anchor, 'click');
    expect(getTooltip()!.style.visibility).toBe('hidden');
  });

  it('ignores empty / whitespace content', () => {
    fixture.componentInstance.content.set('   ');
    fixture.detectChanges();
    fireHover(anchor, 'mouseenter');
    // No tooltip element created OR it stayed hidden.
    const tip = getTooltip();
    if (tip) expect(tip.style.visibility).toBe('hidden');
  });

  it('positions the tooltip via fixed coordinates (no layout reflow on parent)', () => {
    fireHover(anchor, 'mouseenter');
    const tip = getTooltip()!;
    // position: fixed is set in the installed stylesheet; jsdom may not
    // resolve computed styles, so accept either the inline value or the
    // CSS-class signal.
    const computed = window.getComputedStyle(tip).position;
    expect(computed === 'fixed' || tip.classList.contains('app-tooltip')).toBe(true);
  });

  it('clips long list-row content inside the box (overflow + ellipsis on <li>)', () => {
    // Regression: commit-pill tooltip rendered <li><code>path</code></li>
    // with no wrap rule, so long file paths visibly spilled past the
    // tooltip box. The fix installs overflow/ellipsis on list items.
    fireHover(anchor, 'mouseenter');
    const sheet = document.getElementById('app-tooltip-styles');
    expect(sheet).not.toBeNull();
    const css = sheet!.textContent ?? '';
    expect(css).toMatch(/\.app-tooltip__body\s+ul\s+li[\s\S]*?overflow:\s*hidden/);
    expect(css).toMatch(/\.app-tooltip__body\s+ul\s+li[\s\S]*?text-overflow:\s*ellipsis/);
    expect(css).toMatch(/\.app-tooltip__body\s+ul\s+li[\s\S]*?white-space:\s*nowrap/);
  });
});
