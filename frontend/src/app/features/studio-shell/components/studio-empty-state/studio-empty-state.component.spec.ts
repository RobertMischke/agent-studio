import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { StudioEmptyStateComponent } from './studio-empty-state.component';

/**
 * Idle empty-state smoke test. Mounts the Game-of-Life canvas component and
 * tears it down. jsdom returns a null 2D context, so this exercises the
 * null-ctx guards in setup/render plus the lifecycle wiring (timers,
 * IntersectionObserver feature-detection, visibility listener) without a
 * real canvas. A reduced-motion run skips the animation loop entirely.
 */
describe('StudioEmptyStateComponent', () => {
  it('mounts, seeds the grid, and destroys without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioEmptyStateComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(StudioEmptyStateComponent);
    expect(() => fixture.detectChanges()).not.toThrow();
    expect(fixture.componentInstance).toBeTruthy();
    // A subtitle is always present so the empty-state never reads as blank.
    expect(fixture.componentInstance.subtitle().length).toBeGreaterThan(0);
    expect(() => fixture.destroy()).not.toThrow();
  });
});
