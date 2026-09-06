import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
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
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/cli/model-migrations').flush({
      catalogVersion: 'te-2026-09-06',
      migrations: [{
        from: 'claude-haiku-4-5', to: 'claude-sonnet-5', family: 'claude-haiku',
        rule: 'te-economy-haiku-to-sonnet-5', safeAuto: false,
        catalogVersion: 'te-2026-09-06', fromCostClass: 'economy', toCostClass: 'standard',
        fromReasoningLadder: ['low'], toReasoningLadder: ['low', 'medium', 'high'],
      }],
      configPins: [{
        key: 'ClaudeCli:SummaryModel', model: 'claude-haiku-4-5',
        proposal: {
          from: 'claude-haiku-4-5', to: 'claude-sonnet-5', family: 'claude-haiku',
          rule: 'te-economy-haiku-to-sonnet-5', safeAuto: false,
          catalogVersion: 'te-2026-09-06', fromCostClass: 'economy', toCostClass: 'standard',
          fromReasoningLadder: ['low'], toReasoningLadder: ['low', 'medium', 'high'],
        },
      }],
      workspaces: [{ workspaceId: 'ws-1', workspaceName: 'Local', autoApplyEnabled: true }],
    });
    http.expectOne('/api/cli/model-routing/policy').flush({
      version: '2026-07-24',
      wikiPath: 'docs/system/domains/model-routing-policy.md',
      economyMode: false,
      economyModeLabel: 'Economy mode',
      tiers: [],
      taskTypeDefaults: {},
    });
    fixture.detectChanges();

    const groups = fixture.componentInstance.groups();
    expect(groups.length).toBe(CLI_TYPES.length);
    expect(groups.map((g) => g.cliType)).toContain('claude');
    expect(groups.every((g) => typeof g.label === 'string' && g.label.length > 0)).toBe(true);

    const cards = fixture.nativeElement.querySelectorAll('[data-testid^="cli-models-card-"]');
    expect(cards).toHaveLength(CLI_TYPES.length);
    expect(Array.from(cards, (card: Element) => card.getAttribute('data-cli'))).toEqual(CLI_TYPES);
    expect(fixture.nativeElement.querySelector('[data-testid="model-migration-catalog"]')?.textContent)
      .toContain('te-2026-09-06');
    const autoApply = fixture.nativeElement.querySelector(
      '[data-testid="model-migration-auto-ws-1"]',
    ) as HTMLInputElement;
    expect(autoApply.checked).toBe(true);
    const update = fixture.nativeElement.querySelector(
      '[data-testid="config-model-update-ClaudeCli:SummaryModel"] button',
    ) as HTMLButtonElement;
    expect(update.textContent).toContain('Apply');
    update.click();
    const apply = http.expectOne('/api/cli/model-migrations/config-pin/apply');
    expect(apply.request.body).toEqual({
      key: 'ClaudeCli:SummaryModel',
      from: 'claude-haiku-4-5',
      to: 'claude-sonnet-5',
    });
    apply.flush({ applied: true });
    http.expectOne('/api/cli/model-migrations').flush({
      catalogVersion: 'te-2026-09-06', migrations: [], configPins: [], workspaces: [],
    });

    const economy = fixture.nativeElement.querySelector(
      '[data-testid="model-routing-economy-mode"]',
    ) as HTMLInputElement;
    expect(economy.checked).toBe(false);
    economy.click();
    const save = http.expectOne('/api/cli/model-routing/economy-mode');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toEqual({ enabled: true });
    save.flush({ economyMode: true });
    fixture.detectChanges();
    expect(fixture.componentInstance.policy()?.economyMode).toBe(true);
  });
});
