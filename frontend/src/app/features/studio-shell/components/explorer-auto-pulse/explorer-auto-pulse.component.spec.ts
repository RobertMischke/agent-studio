import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ExplorerAutoPulseComponent } from './explorer-auto-pulse.component';

function mount() {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(ExplorerAutoPulseComponent);
  return fixture;
}

describe('ExplorerAutoPulseComponent', () => {
  const dot = (fixture: ReturnType<typeof mount>) =>
    (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('.studio-auto-pulse');

  it('renders an off dot with no accessible role/label and a reserved slot', () => {
    const fixture = mount();
    fixture.componentRef.setInput('state', 'off');
    fixture.componentRef.setInput('testid', 'pulse-x');
    fixture.detectChanges();

    const el = dot(fixture);
    expect(el?.getAttribute('data-pulse')).toBe('off');
    expect(el?.getAttribute('data-testid')).toBe('pulse-x');
    expect(el?.classList.contains('studio-auto-pulse--idle')).toBe(false);
    expect(el?.classList.contains('studio-auto-pulse--active')).toBe(false);
    expect(el?.getAttribute('role')).toBeNull();
    expect(el?.getAttribute('aria-label')).toBeNull();
  });

  it('marks idle and active states with role=img and the given label', () => {
    const fixture = mount();
    fixture.componentRef.setInput('state', 'auto-idle');
    fixture.componentRef.setInput('ariaLabel', 'Auto-pickup on');
    fixture.detectChanges();
    expect(dot(fixture)?.classList.contains('studio-auto-pulse--idle')).toBe(true);
    expect(dot(fixture)?.getAttribute('role')).toBe('img');
    expect(dot(fixture)?.getAttribute('aria-label')).toBe('Auto-pickup on');

    fixture.componentRef.setInput('state', 'auto-active');
    fixture.componentRef.setInput('aggregate', true);
    fixture.detectChanges();
    expect(dot(fixture)?.classList.contains('studio-auto-pulse--active')).toBe(true);
    expect(dot(fixture)?.classList.contains('studio-auto-pulse--aggregate')).toBe(true);
    expect(dot(fixture)?.getAttribute('data-pulse')).toBe('auto-active');
  });
});
