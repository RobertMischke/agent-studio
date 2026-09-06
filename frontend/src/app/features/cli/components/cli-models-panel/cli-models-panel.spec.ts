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
    http.expectOne('/api/cli/model-routing/policy').flush({
      version: '2026-07-24',
      wikiPath: 'docs/system/domains/model-routing-policy.md',
      economyMode: false,
      economyModeLabel: 'Economy mode',
      tiers: [],
      taskTypeDefaults: {},
    });
    http.expectOne('/api/cli/model-migrations').flush({
      version: '2026-09-06.1',
      proposal: null,
      configurationPins: [{
        key: 'ClaudeCli:SummaryModel',
        model: 'claude-opus-4-8',
        proposal: { from: 'claude-opus-4-8', to: 'claude-opus-5' },
      }],
    });
    http.expectOne('/api/workspaces').flush([{
      id: 'workspace-1', displayName: 'Default', sortOrder: 0, isDefault: true,
      color: null, createdAt: '2026-09-06T00:00:00Z', projects: [],
    }]);
    http.expectOne('/api/workspaces/workspace-1/settings').flush({
      orchestratorModel: null,
      orchestratorThinkingLevel: null,
      autonomyLevel: null,
      defaultOrchestratorModel: 'claude-haiku-4-5',
      defaultAutonomyLevel: 2,
      autoApplyModelMigrations: true,
    });
    fixture.detectChanges();

    const groups = fixture.componentInstance.groups();
    expect(groups.length).toBe(CLI_TYPES.length);
    expect(groups.map((g) => g.cliType)).toContain('claude');
    expect(groups.every((g) => typeof g.label === 'string' && g.label.length > 0)).toBe(true);

    const cards = fixture.nativeElement.querySelectorAll('[data-testid^="cli-models-card-"]');
    expect(cards).toHaveLength(CLI_TYPES.length);
    expect(Array.from(cards, (card: Element) => card.getAttribute('data-cli'))).toEqual(CLI_TYPES);

    const economy = fixture.nativeElement.querySelector(
      '[data-testid="model-routing-economy-mode"]',
    ) as HTMLInputElement;
    expect(economy.checked).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('Token Economy migration catalog 2026-09-06.1');
    expect(fixture.nativeElement.textContent).toContain('1 configuration update available');
    expect(fixture.nativeElement.textContent).toContain('claude-opus-4-8 → claude-opus-5');
    const applyConfiguration = fixture.nativeElement.querySelector(
      '[data-testid="configuration-model-migration-ClaudeCli:SummaryModel"] button',
    ) as HTMLButtonElement;
    applyConfiguration.click();
    const configurationSave = http.expectOne('/api/cli/model-migrations/configuration-pin/apply');
    expect(configurationSave.request.body).toEqual({ key: 'ClaudeCli:SummaryModel' });
    configurationSave.flush({
      key: 'ClaudeCli:SummaryModel',
      model: 'claude-opus-5',
      proposal: { from: 'claude-opus-4-8', to: 'claude-opus-5' },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('claude-opus-4-8 → claude-opus-5');
    const autoApply = fixture.nativeElement.querySelector(
      '[data-testid="model-migration-auto-apply"]',
    ) as HTMLInputElement;
    expect(autoApply.checked).toBe(true);
    autoApply.click();
    const autoApplySave = http.expectOne('/api/workspaces/workspace-1/model-migration-auto-apply');
    expect(autoApplySave.request.body).toEqual({ enabled: false });
    autoApplySave.flush({ enabled: false });
    economy.click();
    const save = http.expectOne('/api/cli/model-routing/economy-mode');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toEqual({ enabled: true });
    save.flush({ economyMode: true });
    fixture.detectChanges();
    expect(fixture.componentInstance.policy()?.economyMode).toBe(true);
  });
});
