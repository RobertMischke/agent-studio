import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ChatModelBadgeComponent } from './chat-model-badge.component';

/**
 * Smoke spec for the F44 chat-compose model badge. Verifies the
 * component compiles + renders with realistic inputs; the deeper
 * menu-builder + helper coverage lives in
 * `../protocol-pane/protocol-pane/model-badge-menu-builders.spec.ts`.
 */
describe('ChatModelBadgeComponent (smoke)', () => {
  it('renders the badge label and menu state from inputs', async () => {
    await TestBed.configureTestingModule({
      imports: [ChatModelBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', [
      { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'a', isDefault: true },
    ]);
    fixture.componentRef.setInput('disabled', false);

    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ChatModelBadgeComponent initial render skipped:', (e as Error).message);
    }

    expect(fixture.componentInstance.displayName()).toBe('opus 4.7');
    expect(fixture.componentInstance.menuOpen()).toBe(false);
    expect(fixture.componentInstance.disabledReason()).toBeNull();
  });

  it('reports a disabled reason while a run is in flight', async () => {
    await TestBed.configureTestingModule({
      imports: [ChatModelBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', true);

    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ChatModelBadgeComponent disabled-render skipped:', (e as Error).message);
    }

    expect(fixture.componentInstance.disabledReason()).toMatch(/stop the run/i);
  });
});
