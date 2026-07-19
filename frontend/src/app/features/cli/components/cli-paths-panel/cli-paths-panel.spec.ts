import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliPathsPanelComponent } from './cli-paths-panel';

/**
 * Smoke + one projection check. Compiles + instantiates the standalone
 * component and asserts the `groups` projection folds a stubbed usage
 * report into per-CLI path rows (executable path + project roots).
 */
describe('CliPathsPanelComponent', () => {
  it('projects a usage report into per-CLI path groups', async () => {
    await TestBed.configureTestingModule({
      imports: [CliPathsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(CliPathsPanelComponent);
    const cmp = fixture.componentInstance;

    cmp.report.set({
      at: new Date().toISOString(),
      sections: [
        {
          cliType: 'claude',
          available: true,
          version: '1.2.3',
          path: '/usr/bin/claude',
          error: null,
          projects: [
            { projectName: 'beta', rootPath: '/repos/beta', sessions: [{ id: 's2' } as never] },
            { projectName: 'alpha', rootPath: '/repos/alpha', sessions: [] },
            { projectName: 'nopath', rootPath: null, sessions: [{ id: 's3' } as never] },
          ],
        },
      ],
    });

    const groups = cmp.groups();
    expect(groups).toHaveLength(1);
    const g = groups[0];
    expect(g.cliType).toBe('claude');
    expect(g.executablePath).toBe('/usr/bin/claude');
    // rootPath-less projects are dropped; the rest sort by project name.
    expect(g.roots.map((r) => r.projectName)).toEqual(['alpha', 'beta']);
    expect(g.roots[1].sessionCount).toBe(1);
  });
});
