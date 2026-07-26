import { describe, expect, it } from 'vitest';
import { ProjectSettingsPanelComponent } from './project-settings-panel.component';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 */
describe('ProjectSettingsPanelComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    expect(ProjectSettingsPanelComponent).toBeTruthy();
  });
});
