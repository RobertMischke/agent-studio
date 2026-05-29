import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { perfEnabled, perfMark, perfMeasure, resetPerfEnabledCache } from './perf-tracker';

/**
 * Locks the gate contract: instrumentation is OFF by default and only
 * flips on when the operator opts in via `?perf=1` or
 * `localStorage.perf === '1'`. This keeps the marks shipped without
 * paying for them on a normal session.
 */
describe('perf-tracker gate', () => {
  beforeEach(() => {
    resetPerfEnabledCache();
    try { window.localStorage.removeItem('perf'); } catch { /* env may lack it */ }
    // Reset the URL search so a previous test's ?perf=1 doesn't bleed in.
    history.replaceState(null, '', window.location.pathname);
  });

  afterEach(() => {
    try { window.localStorage.removeItem('perf'); } catch { /* ignore */ }
    history.replaceState(null, '', window.location.pathname);
    resetPerfEnabledCache();
  });

  it('is OFF by default — perfMark does not call performance.mark', () => {
    const spy = vi.spyOn(performance, 'mark');
    expect(perfEnabled()).toBe(false);
    perfMark('a');
    expect(spy).not.toHaveBeenCalled();
    spy.mockRestore();
  });

  it('opt-in via ?perf=1 enables the marks', () => {
    history.replaceState(null, '', `${window.location.pathname}?perf=1`);
    resetPerfEnabledCache();
    const spy = vi.spyOn(performance, 'mark');
    expect(perfEnabled()).toBe(true);
    perfMark('b-start');
    perfMark('b-end');
    expect(spy).toHaveBeenCalledWith('b-start');
    expect(spy).toHaveBeenCalledWith('b-end');
    spy.mockRestore();
  });

  it('opt-in via localStorage.perf=1 enables the marks', () => {
    window.localStorage.setItem('perf', '1');
    resetPerfEnabledCache();
    const spy = vi.spyOn(performance, 'mark');
    perfMark('c');
    expect(spy).toHaveBeenCalledWith('c');
    spy.mockRestore();
  });

  it('perfMeasure emits a console line when the gate is on and both marks were recorded', () => {
    window.localStorage.setItem('perf', '1');
    resetPerfEnabledCache();
    const consoleSpy = vi.spyOn(console, 'info').mockImplementation(() => undefined);
    perfMark('d-start');
    perfMark('d-end');
    perfMeasure('d-span', 'd-start', 'd-end');
    expect(consoleSpy).toHaveBeenCalled();
    const msg = String(consoleSpy.mock.calls[0]?.[0] ?? '');
    expect(msg).toMatch(/^\[perf\] d-span:/);
    consoleSpy.mockRestore();
  });
});
