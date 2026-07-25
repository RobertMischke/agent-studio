import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ExplorerAutoPickupIndicatorComponent } from './explorer-auto-pickup-indicator.component';

function mount() {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(ExplorerAutoPickupIndicatorComponent);
  return fixture;
}

describe('ExplorerAutoPickupIndicatorComponent', () => {
  const dot = (fixture: ReturnType<typeof mount>) =>
    (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('.studio-auto-pickup');

  it('renders an off aggregate with no accessible role or label', () => {
    const fixture = mount();
    fixture.componentRef.setInput('state', 'off');
    fixture.componentRef.setInput('testid', 'pickup-x');
    fixture.detectChanges();

    const el = dot(fixture);
    expect(el?.getAttribute('data-auto-pickup-state')).toBe('off');
    expect(el?.getAttribute('data-testid')).toBe('pickup-x');
    expect(el?.getAttribute('role')).toBeNull();
    expect(el?.getAttribute('aria-label')).toBeNull();
  });

  it('renders every project state with a stable accessible slot', () => {
    const fixture = mount();
    for (const state of ['active', 'paused', 'manual', 'blocked'] as const) {
      fixture.componentRef.setInput('state', state);
      fixture.componentRef.setInput('tooltip', `State: ${state}`);
      fixture.componentRef.setInput('reason', state === 'blocked' ? 'build profile declared' : null);
      fixture.detectChanges();
      expect(dot(fixture)?.classList.contains(`studio-auto-pickup--${state}`)).toBe(true);
      expect(dot(fixture)?.getAttribute('role')).toBe('img');
      expect(dot(fixture)?.getAttribute('aria-label')).toBe(`State: ${state}`);
      expect(dot(fixture)?.getAttribute('data-auto-pickup-state')).toBe(state);
    }
    expect(dot(fixture)?.getAttribute('data-auto-pickup-reason')).toBe('build profile declared');
  });
});
