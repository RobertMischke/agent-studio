import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CreateModePickerComponent } from './create-mode-picker.component';

describe('CreateModePickerComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(CreateModePickerComponent);
    fixture.detectChanges();
    return { fixture, cmp: fixture.componentInstance };
  }

  it('defaults to coding with web access off', () => {
    const { cmp } = setup();
    expect(cmp.mode()).toBe('coding');
    expect(cmp.allowWebAccess()).toBe(false);
  });

  it('turns web access on by default when research is chosen', () => {
    const { cmp } = setup();
    cmp.setMode('research');
    expect(cmp.mode()).toBe('research');
    expect(cmp.allowWebAccess()).toBe(true);
  });

  it('keeps web access off for planning', () => {
    const { cmp } = setup();
    cmp.setMode('planning');
    expect(cmp.mode()).toBe('planning');
    expect(cmp.allowWebAccess()).toBe(false);
  });

  it('offers concept mode with web access off', () => {
    const { cmp } = setup();
    expect(cmp.modeOptions.some((option) => option.value === 'concept')).toBe(true);
    cmp.setMode('concept');
    expect(cmp.mode()).toBe('concept');
    expect(cmp.allowWebAccess()).toBe(false);
  });

  it('resets web access to the mode default when switching back to coding', () => {
    const { cmp } = setup();
    cmp.setMode('research');
    expect(cmp.allowWebAccess()).toBe(true);
    cmp.setMode('coding');
    expect(cmp.mode()).toBe('coding');
    expect(cmp.allowWebAccess()).toBe(false);
  });

  it('lets the user override the web toggle independently of the mode', () => {
    const { cmp } = setup();
    expect(cmp.allowWebAccess()).toBe(false);
    cmp.toggleWebAccess();
    expect(cmp.allowWebAccess()).toBe(true);
    cmp.toggleWebAccess();
    expect(cmp.allowWebAccess()).toBe(false);
  });

  it('exposes the per-mode web default (research = on, else off)', () => {
    expect(CreateModePickerComponent.webDefaultFor('research')).toBe(true);
    expect(CreateModePickerComponent.webDefaultFor('planning')).toBe(false);
    expect(CreateModePickerComponent.webDefaultFor('concept')).toBe(false);
    expect(CreateModePickerComponent.webDefaultFor('coding')).toBe(false);
  });
});
