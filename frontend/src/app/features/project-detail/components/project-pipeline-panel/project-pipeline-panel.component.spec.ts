import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectPipelinePanelComponent } from './project-pipeline-panel.component';
import type { PipelineCatalogueStep, PipelineStepSetting } from '../../../task-pipeline';
import type { ProjectPipelineCostTimeline } from '../../../project-token-usage';

/**
 * Nav-rebuild T4a smoke. Compiles + instantiates the standalone component so a
 * broken templateUrl/styleUrl, inject() wiring, or signal init regresses the
 * test rather than only surfacing in a browser. `detectChanges()` is guarded
 * because the projectName effect issues pending test HTTP calls.
 */
describe('ProjectPipelinePanelComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelinePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectPipelinePanelComponent);
    fixture.componentRef.setInput('projectName', undefined);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ProjectPipelinePanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Render check for the configuration grid + prompt-binding cell. Seeds the
 * catalogue / overrides / cost signals directly (the effect's HTTP loads stay
 * pending under provideHttpClientTesting and only set on `next`, so seeded
 * state survives). Asserts: a phase group renders; a configurable aspect step
 * exposes its enable toggle while a core step is locked "always on"; the prompt
 * cell shows the registry template reference plus the legacy inline-override
 * Clear affordance; and the per-kind cost legend + total render.
 */
describe('ProjectPipelinePanelComponent (render)', () => {
  function catalogue(): PipelineCatalogueStep[] {
    return [
      {
        id: 'core-run', displayName: 'Agent run', kind: 'core', phase: 'core',
        usesModel: true, usesPrompt: false, supportsMode: false, cliType: 'claude',
        promptTemplate: null, canDisable: false, defaultEnabled: true, supportsCondition: false,
      },
      {
        id: 'aspect-requirement-fit', displayName: 'Requirement fit', kind: 'aspect', phase: 'aspect',
        usesModel: true, usesPrompt: true, supportsMode: true, cliType: 'claude',
        promptTemplate: 'aspect-requirement-fit', canDisable: true, defaultEnabled: true, supportsCondition: true,
      },
    ];
  }

  function overrides(): Record<string, PipelineStepSetting> {
    return {
      'aspect-requirement-fit': { enabled: true, mode: 'warn', prompt: 'legacy inline text' },
    };
  }

  function fakeCost(): ProjectPipelineCostTimeline {
    const days = ['2026-06-08', '2026-06-09', '2026-06-10'];
    return {
      project: 'demo', days, windowDays: 30,
      kinds: [
        { kind: 'core', totalTokens: 300_000, totalCostUsd: 0.75, anyModelUnknown: false, cells: [] },
        { kind: 'aspect', totalTokens: 80_000, totalCostUsd: 0.08, anyModelUnknown: true, cells: [] },
      ],
      totalTokens: 380_000, totalCostUsd: 0.83, anyModelUnknown: true,
      taskCount: 4, hasData: true, fetchedAt: '2026-06-10T00:00:00Z',
    };
  }

  it('renders groups, the prompt binding cell, and the cost legend', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelinePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectPipelinePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }

    fixture.componentInstance.catalogue.set(catalogue());
    fixture.componentInstance.overrides.set(overrides());
    fixture.componentInstance.pipelineCost.set(fakeCost());
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="project-detail-pipeline"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-empty"]')).toBeNull();

    // Phase groups + both step rows.
    expect(host.querySelector('[data-testid="pipeline-group-core"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-group-aspect"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-row-core-run"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-row-aspect-requirement-fit"]')).toBeTruthy();

    // Core step is locked on; the aspect step exposes its enable toggle.
    expect(host.querySelector('[data-testid="pipeline-step-enabled-core-run"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-step-enabled-aspect-requirement-fit"]')).toBeTruthy();

    // Prompt binding cell: registry template reference + legacy inline override + Clear.
    const promptCell = host.querySelector('[data-testid="pipeline-step-prompt-aspect-requirement-fit"]');
    expect(promptCell?.textContent).toContain('aspect-requirement-fit');
    expect(host.querySelector('[data-testid="pipeline-step-prompt-clear-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-prompt-manage-aspect-requirement-fit"]')).toBeTruthy();

    // Cost legend: per-kind rows + total, with the unknown-price star.
    expect(host.querySelector('[data-testid="pipeline-cost-empty"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-core"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-aspect"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-total"]')?.textContent).toContain('$0.83');
  });

  it('emits openPrompts when Manage in Prompts is clicked', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelinePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectPipelinePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }

    fixture.componentInstance.catalogue.set(catalogue());
    fixture.componentInstance.overrides.set(overrides());
    fixture.detectChanges();

    let opened = 0;
    fixture.componentInstance.openPrompts.subscribe(() => opened++);

    const manage = host(fixture).querySelector<HTMLButtonElement>(
      '[data-testid="pipeline-step-prompt-manage-aspect-requirement-fit"]',
    );
    manage?.click();
    expect(opened).toBe(1);
  });
});

function host(fixture: { nativeElement: HTMLElement }): HTMLElement {
  return fixture.nativeElement;
}
