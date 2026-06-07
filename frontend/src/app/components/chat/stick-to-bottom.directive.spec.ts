import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { Component, ElementRef, ViewChild, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { StickToBottomDirective } from './stick-to-bottom.directive';

@Component({
  standalone: true,
  imports: [StickToBottomDirective],
  template: `
    <div #scroller class="scroller" style="overflow-y: auto">
      <section appStickToBottom #stick="stickToBottom">
        <div class="content"></div>
      </section>
      <textarea class="composer"></textarea>
    </div>
  `,
})
class HostComponent {
  @ViewChild('stick') dir!: StickToBottomDirective;
  @ViewChild('scroller') scroller!: ElementRef<HTMLDivElement>;
}

/**
 * jsdom has no layout engine, so scrollHeight/clientHeight are faked per
 * element and requestAnimationFrame is made synchronous to keep the pin
 * deterministic. The directive's geometry maths is what we exercise here;
 * real browser pinning is covered by the e2e spec.
 */
function fakeGeometry(el: HTMLElement, scrollHeight: number, clientHeight: number): void {
  Object.defineProperty(el, 'scrollHeight', { value: scrollHeight, configurable: true });
  Object.defineProperty(el, 'clientHeight', { value: clientHeight, configurable: true });
}

let resizeCallback: (() => void) | null = null;
const realRaf = globalThis.requestAnimationFrame;
const realCaf = globalThis.cancelAnimationFrame;
const realRO = (globalThis as any).ResizeObserver;

describe('StickToBottomDirective', () => {
  let queuedRafs: FrameRequestCallback[] | null;

  beforeEach(() => {
    resizeCallback = null;
    queuedRafs = null;
    // Synchronous rAF so the pin lands within the test tick.
    (globalThis as any).requestAnimationFrame = (cb: FrameRequestCallback) => {
      cb(0);
      return 0 as unknown as number;
    };
    (globalThis as any).cancelAnimationFrame = () => undefined;
    (globalThis as any).ResizeObserver = class {
      constructor(cb: () => void) {
        resizeCallback = cb;
      }
      observe(): void {}
      disconnect(): void {}
    };
  });

  afterEach(() => {
    queuedRafs = null;
    globalThis.requestAnimationFrame = realRaf;
    globalThis.cancelAnimationFrame = realCaf;
    (globalThis as any).ResizeObserver = realRO;
  });

  async function mount() {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges(); // renders + ngAfterViewInit (resolve container, initial pin)
    const scroller = fixture.nativeElement.querySelector('.scroller') as HTMLDivElement;
    fakeGeometry(scroller, 1000, 200);
    return { fixture, scroller, dir: fixture.componentInstance.dir };
  }

  it('starts stuck', async () => {
    const { dir } = await mount();
    expect(dir.stuck()).toBe(true);
  });

  it('releases when the user scrolls up past the threshold', async () => {
    const { scroller, dir } = await mount();
    scroller.scrollTop = 0; // 800px from bottom
    scroller.dispatchEvent(new Event('scroll'));
    expect(dir.stuck()).toBe(false);
  });

  it('re-sticks when the user scrolls back near the bottom', async () => {
    const { scroller, dir } = await mount();
    scroller.scrollTop = 0;
    scroller.dispatchEvent(new Event('scroll'));
    expect(dir.stuck()).toBe(false);
    scroller.scrollTop = 790; // 10px from bottom, within 24px threshold
    scroller.dispatchEvent(new Event('scroll'));
    expect(dir.stuck()).toBe(true);
  });

  it('scrollToBottom() re-pins the container and resumes sticking', async () => {
    const { scroller, dir } = await mount();
    scroller.scrollTop = 0;
    scroller.dispatchEvent(new Event('scroll'));
    expect(dir.stuck()).toBe(false);

    dir.scrollToBottom();
    expect(dir.stuck()).toBe(true);
    expect(scroller.scrollTop).toBe(1000);
  });

  it('re-pins on content growth while stuck', async () => {
    const { scroller } = await mount();
    scroller.scrollTop = 0;
    expect(resizeCallback).toBeTypeOf('function');
    resizeCallback!(); // simulate content growth while still stuck
    expect(scroller.scrollTop).toBe(1000);
  });

  it('does NOT re-pin on content growth after the user scrolled up', async () => {
    const { scroller, dir } = await mount();
    scroller.scrollTop = 0;
    scroller.dispatchEvent(new Event('scroll'));
    expect(dir.stuck()).toBe(false);
    scroller.scrollTop = 0;
    resizeCallback!();
    expect(scroller.scrollTop).toBe(0); // stayed put — user's scroll position respected
  });

  it('does NOT re-pin on content growth while the composer has focus', async () => {
    const { fixture, scroller } = await mount();
    const textarea = fixture.nativeElement.querySelector('.composer') as HTMLTextAreaElement;
    scroller.scrollTop = 500;
    textarea.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    resizeCallback!();
    expect(scroller.scrollTop).toBe(500);
  });

  it('cancels a pending re-pin if the composer receives focus before the frame runs', async () => {
    queuedRafs = [];
    (globalThis as any).requestAnimationFrame = (cb: FrameRequestCallback) => {
      queuedRafs!.push(cb);
      return queuedRafs!.length as unknown as number;
    };
    (globalThis as any).cancelAnimationFrame = () => undefined;

    const { fixture, scroller, dir } = await mount();
    fakeGeometry(scroller, 1000, 200);
    scroller.scrollTop = 500;
    expect(dir.stuck()).toBe(true);

    resizeCallback!();
    const textarea = fixture.nativeElement.querySelector('.composer') as HTMLTextAreaElement;
    textarea.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    queuedRafs.shift()?.(0);

    expect(scroller.scrollTop).toBe(500);
  });

  // Requirement 2 (no excess whitespace): the pin requests the maximum scroll
  // offset, so nothing reachable is left empty below the last row. In a real
  // browser scrollTop clamps to scrollHeight - clientHeight; here we assert the
  // post-pin distance-from-bottom is non-positive (fully consumed).
  it('pins to the maximum offset, leaving no reachable whitespace below', async () => {
    const { scroller } = await mount();
    resizeCallback!(); // content settled / grew while stuck → re-pin
    const distanceFromBottom = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;
    expect(distanceFromBottom).toBeLessThanOrEqual(0);
  });

  // Requirement 3 (stable display): small scrolls that stay within the
  // threshold must not flap the stuck state, which would otherwise toggle the
  // jump affordance and trigger re-pin churn.
  it('stays stuck through sub-threshold scroll jitter (no flapping)', async () => {
    const { scroller, dir } = await mount(); // scrollHeight 1000, clientHeight 200
    for (const top of [790, 780, 795, 777]) {
      scroller.scrollTop = top; // distanceFromBottom 10/20/5/23 — all ≤ 24
      scroller.dispatchEvent(new Event('scroll'));
      expect(dir.stuck()).toBe(true);
    }
  });
});
