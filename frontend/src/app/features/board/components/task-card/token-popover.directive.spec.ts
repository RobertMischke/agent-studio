import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TaskService } from '../../../../services/task.service';
import { TokenPopoverDirective } from './token-popover.directive';

@Component({
  selector: 'app-token-popover-test-host',
  standalone: true,
  imports: [TokenPopoverDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-popover.directive.spec.html',
})
class TokenPopoverTestHostComponent {}

class FakeIntersectionObserver {
  static latest: FakeIntersectionObserver | null = null;

  private target: Element | null = null;

  constructor(private readonly callback: IntersectionObserverCallback) {
    FakeIntersectionObserver.latest = this;
  }

  observe(target: Element): void {
    this.target = target;
  }

  disconnect(): void {
    this.target = null;
  }

  unobserve(target: Element): void {
    if (this.target === target) this.target = null;
  }

  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }

  emit(isIntersecting: boolean): void {
    if (!this.target) return;
    this.callback([{
      target: this.target,
      isIntersecting,
    } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
  }
}

describe('TokenPopoverDirective', () => {
  let fixture: ComponentFixture<TokenPopoverTestHostComponent>;
  let boardSnapshot: ReturnType<typeof signal<object>>;
  let originalIntersectionObserver: typeof IntersectionObserver | undefined;

  beforeEach(async () => {
    vi.useFakeTimers();
    boardSnapshot = signal<object>({ revision: 0 });
    originalIntersectionObserver = globalThis.IntersectionObserver;
    globalThis.IntersectionObserver = FakeIntersectionObserver as unknown as typeof IntersectionObserver;

    await TestBed.configureTestingModule({
      imports: [TokenPopoverTestHostComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { grouped: boardSnapshot } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TokenPopoverTestHostComponent);
    fixture.detectChanges();
    TestBed.tick();
  });

  afterEach(() => {
    fixture.destroy();
    document.body.querySelector('.studio-overlay-root')?.remove();
    FakeIntersectionObserver.latest = null;
    if (originalIntersectionObserver) {
      globalThis.IntersectionObserver = originalIntersectionObserver;
    } else {
      delete (globalThis as { IntersectionObserver?: typeof IntersectionObserver }).IntersectionObserver;
    }
    vi.useRealTimers();
  });

  it('opens after hover intent, keeps only the latest panel open, and closes on board refresh', () => {
    const firstTrigger = fixture.nativeElement.querySelector('[data-testid="token-trigger-a"]') as HTMLElement;
    const secondTrigger = fixture.nativeElement.querySelector('[data-testid="token-trigger-b"]') as HTMLElement;
    const firstPanel = fixture.nativeElement.querySelector('[data-owner="a"]') as HTMLElement;
    const secondPanel = fixture.nativeElement.querySelector('[data-owner="b"]') as HTMLElement;

    expect(firstPanel.hidden).toBe(true);
    expect(secondPanel.hidden).toBe(true);

    firstTrigger.dispatchEvent(new MouseEvent('mouseenter'));
    vi.advanceTimersByTime(299);
    expect(firstPanel.hidden).toBe(true);
    vi.advanceTimersByTime(1);
    expect(firstPanel.hidden).toBe(false);

    secondTrigger.dispatchEvent(new MouseEvent('mouseenter'));
    vi.advanceTimersByTime(300);
    expect(firstPanel.hidden).toBe(true);
    expect(secondPanel.hidden).toBe(false);
    expect(document.querySelectorAll('.studio-overlay-root [data-token-popover]')).toHaveLength(1);

    boardSnapshot.set({ revision: 1 });
    TestBed.tick();
    expect(secondPanel.hidden).toBe(true);
    expect(document.querySelectorAll('.studio-overlay-root [data-token-popover]')).toHaveLength(0);
  });

  it('dismisses on outside pointer down, Escape, and lane scroll', () => {
    const trigger = fixture.nativeElement.querySelector('[data-testid="token-trigger-a"]') as HTMLElement;
    const panel = fixture.nativeElement.querySelector('[data-owner="a"]') as HTMLElement;
    const lane = fixture.nativeElement.querySelector('[data-testid="test-lane-scroll"]') as HTMLElement;

    trigger.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    expect(panel.hidden).toBe(false);
    document.body.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }));
    expect(panel.hidden).toBe(true);

    trigger.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    expect(panel.hidden).toBe(false);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    expect(panel.hidden).toBe(true);

    trigger.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    expect(panel.hidden).toBe(false);
    lane.dispatchEvent(new Event('scroll'));
    expect(panel.hidden).toBe(true);
  });

  it('dismisses when the anchor card leaves the viewport', () => {
    const trigger = fixture.nativeElement.querySelector('[data-testid="token-trigger-a"]') as HTMLElement;
    const panel = fixture.nativeElement.querySelector('[data-owner="a"]') as HTMLElement;

    trigger.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    expect(panel.hidden).toBe(false);

    FakeIntersectionObserver.latest?.emit(false);
    expect(panel.hidden).toBe(true);
  });
});
