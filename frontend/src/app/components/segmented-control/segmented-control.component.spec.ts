import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { SegmentedControlComponent } from './segmented-control.component';

/**
 * Render-path spec for the shared segmented control. Pins the load-bearing
 * accessibility + selection contract the Settings toggles rely on:
 *   - one button per option, group labelled for assistive tech,
 *   - exactly the selected option carries aria-pressed="true",
 *   - clicking another option emits its value (and re-clicking the active
 *     option is a no-op).
 */
describe('SegmentedControlComponent', () => {
  function setup(value: 'dark' | 'light') {
    TestBed.configureTestingModule({
      imports: [SegmentedControlComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(SegmentedControlComponent<'dark' | 'light'>);
    fixture.componentRef.setInput('options', [
      { value: 'dark', label: 'Dark', testid: 'opt-dark' },
      { value: 'light', label: 'Light', testid: 'opt-light' },
    ]);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('ariaLabel', 'Theme');
    fixture.detectChanges();
    return fixture;
  }

  it('renders one button per option inside a labelled group', () => {
    const fixture = setup('light');
    const group = fixture.nativeElement.querySelector('[role="group"]');
    expect(group?.getAttribute('aria-label')).toBe('Theme');
    const buttons = fixture.nativeElement.querySelectorAll('button.segmented__option');
    expect(buttons.length).toBe(2);
  });

  it('marks only the selected option as pressed/active', () => {
    const fixture = setup('light');
    const dark = fixture.nativeElement.querySelector('[data-testid="opt-dark"]');
    const light = fixture.nativeElement.querySelector('[data-testid="opt-light"]');
    expect(light.getAttribute('aria-pressed')).toBe('true');
    expect(dark.getAttribute('aria-pressed')).toBe('false');
    expect(light.classList.contains('segmented__option--active')).toBe(true);
    expect(dark.classList.contains('segmented__option--active')).toBe(false);
  });

  it('emits the value when a different option is clicked, and ignores the active one', () => {
    const fixture = setup('light');
    const emitted: string[] = [];
    fixture.componentInstance.valueChange.subscribe((v) => emitted.push(v));

    fixture.nativeElement.querySelector('[data-testid="opt-dark"]').click();
    fixture.nativeElement.querySelector('[data-testid="opt-light"]').click(); // active → no-op
    expect(emitted).toEqual(['dark']);
  });
});
