import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliModelsPanelComponent } from './cli-models-panel';
import { CLI_TYPES } from '../../../../models/task.model';

/**
 * Smoke + one behavioural check. Compiles + instantiates the standalone
 * component and asserts it renders one group per known CLI (the groups
 * computed walks CLI_TYPES regardless of whether any catalog is loaded).
 */
describe('CliModelsPanelComponent', () => {
  it('compiles and produces one group per known CLI', async () => {
    await TestBed.configureTestingModule({
      imports: [CliModelsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliModelsPanelComponent);
    const groups = fixture.componentInstance.groups();
    expect(groups.length).toBe(CLI_TYPES.length);
    expect(groups.map((g) => g.cliType)).toContain('claude');
    expect(groups.every((g) => typeof g.label === 'string' && g.label.length > 0)).toBe(true);
  });
});
