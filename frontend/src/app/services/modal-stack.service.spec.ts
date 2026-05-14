import { TestBed } from '@angular/core/testing';
import { ModalStackService } from './modal-stack.service';

function pressEscape(): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
  document.dispatchEvent(event);
  return event;
}

describe('ModalStackService', () => {
  let service: ModalStackService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [ModalStackService] });
    service = TestBed.inject(ModalStackService);
    service.clearForTest();
  });

  it('does nothing when the stack is empty', () => {
    const event = pressEscape();
    expect(event.defaultPrevented).toBe(false);
    expect(service.hasOpen()).toBe(false);
  });

  it('closes only the topmost entry on Escape', () => {
    const calls: string[] = [];
    service.push('detail', () => { calls.push('detail'); });
    service.push('add-task', () => { calls.push('add-task'); });

    const event = pressEscape();

    expect(calls).toEqual(['add-task']);
    expect(event.defaultPrevented).toBe(true);
    expect(service.depth()).toBe(2);
  });

  it('after the top entry disposes, Escape closes the next entry below', () => {
    const calls: string[] = [];
    service.push('detail', () => { calls.push('detail'); });
    const disposeAdd = service.push('add-task', () => { calls.push('add-task'); });

    disposeAdd();
    expect(service.topId()).toBe('detail');

    const event = pressEscape();
    expect(calls).toEqual(['detail']);
    expect(event.defaultPrevented).toBe(true);
  });

  it('disposer is idempotent', () => {
    const calls: string[] = [];
    const dispose = service.push('a', () => { calls.push('a'); });
    dispose();
    dispose();
    expect(service.depth()).toBe(0);
  });

  it('ignores Escape with modifier keys', () => {
    let closed = false;
    service.push('a', () => { closed = true; });

    const event = new KeyboardEvent('keydown', { key: 'Escape', ctrlKey: true, bubbles: true, cancelable: true });
    document.dispatchEvent(event);

    expect(closed).toBe(false);
    expect(event.defaultPrevented).toBe(false);
  });

  it('ignores non-Escape keys', () => {
    let closed = false;
    service.push('a', () => { closed = true; });

    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    document.dispatchEvent(event);

    expect(closed).toBe(false);
    expect(event.defaultPrevented).toBe(false);
  });

  it('a handler that returns false declines and lets propagation continue', () => {
    let topClosed = false;
    let underClosed = false;
    service.push('under', () => { underClosed = true; });
    service.push('declining', () => { topClosed = true; return false; });

    const event = pressEscape();
    expect(topClosed).toBe(true);
    expect(underClosed).toBe(false);
    expect(event.defaultPrevented).toBe(false);
  });

  it('a faulty close handler does not lock the stack', () => {
    const calls: string[] = [];
    service.push('a', () => { calls.push('a'); });
    service.push('b-bad', () => { throw new Error('boom'); });

    pressEscape();
    expect(calls).toEqual([]);
    expect(service.depth()).toBe(2);

    // Bad handler stayed on the stack; the caller is expected to dispose it.
    // Subsequent Escape will retry on the same top entry — the service does
    // not auto-pop, the owning component must.
  });
});
