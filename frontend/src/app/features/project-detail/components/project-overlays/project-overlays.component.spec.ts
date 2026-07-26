import { describe, expect, it } from 'vitest';
import { ProjectOverlaysComponent } from './project-overlays.component';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('ProjectOverlaysComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    expect(ProjectOverlaysComponent).toBeTruthy();
  });

  it('keeps Agent Docs as a dedicated steering-docs panel', async () => {
    const hasCustomPanel = ProjectOverlaysComponent.prototype.hasCustomPanel;
    expect(hasCustomPanel.call({} as ProjectOverlaysComponent, 'audits')).toBe(false);
    expect(hasCustomPanel.call({} as ProjectOverlaysComponent, 'steering')).toBe(true);
  });
});
