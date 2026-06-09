import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { CliModelSelectorComponent } from './cli-model-selector.component';
import type { CliModelInfo } from '../../features/cli';
import { CliCatalogStore } from '../../services/cli-catalog.store';
import { TaskService } from '../../services/task.service';

/**
 * Behavioural specs for the unified CLI + model picker. Generalised from
 * the historical `chat-model-badge` specs - the popover semantics
 * (CLI-switch keeps it open, model click without CLI change auto-commits,
 * Cancel reverts) must hold at every call-site.
 */
describe('CliModelSelectorComponent', () => {
  const claudeModels: CliModelInfo[] = [
    { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'anthropic', isDefault: true, thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
    { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', multiplier: 1, vendor: 'anthropic', isDefault: false, thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
    { id: 'claude-haiku-4-5', label: 'Haiku 4.5', multiplier: 1, vendor: 'anthropic', isDefault: false, thinkingLevels: [], defaultThinkingLevel: null },
  ];

  function configure() {
    return TestBed.configureTestingModule({
      imports: [CliModelSelectorComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
  }

  it('renders the chip label from inputs and starts with the picker closed', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('thinkingLevel', 'high');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance.displayName()).toBe('opus 4.7');
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
    expect(fixture.componentInstance.effectiveDisabledReason()).toBeNull();
  });

  it('reports a disabled reason while a run is in flight', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', true);
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(fixture.componentInstance.effectiveDisabledReason()).toMatch(/stop the run/i);
  });

  it('lets callers override the disabled reason', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', true);
    fixture.componentRef.setInput('disabledReason', 'Read-only in archived tasks');
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(fixture.componentInstance.effectiveDisabledReason()).toBe('Read-only in archived tasks');
  });

  it('seeds the draft from inputs when the picker opens', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    expect(fixture.componentInstance.pickerOpen()).toBe(true);
    expect(fixture.componentInstance.draftCliType()).toBe('claude');
    expect(fixture.componentInstance.draftModel()).toBe('claude-opus-4-7');
    expect(fixture.componentInstance.draftAvailableModels()).toEqual(claudeModels);
    expect(fixture.componentInstance.hasChanges()).toBe(false);
  });

  it('does not offer unavailable catalog models', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', [
      ...claudeModels,
      { id: 'claude-opus-4-6', label: 'Opus 4.6', multiplier: null, vendor: 'anthropic', isDefault: false, available: false, deprecated: true },
    ]);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.openPicker(new MouseEvent('click'));

    expect(fixture.componentInstance.draftAvailableModels().map(m => m.id)).not.toContain('claude-opus-4-6');
  });

  it('clicking a model pill without a CLI change auto-commits + closes', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const commits: { cliType: string; model: string; thinkingLevel: string | null }[] = [];
    const modelChanges: string[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));
    fixture.componentInstance.modelChange.subscribe((m) => modelChanges.push(m));

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillClick('claude-sonnet-4-6');

    expect(commits).toEqual([{ cliType: 'claude', model: 'claude-sonnet-4-6', thinkingLevel: 'high' }]);
    expect(modelChanges).toEqual(['claude-sonnet-4-6']);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });

  it('after a CLI switch, model click keeps the picker open until Done', async () => {
    const codexModels: CliModelInfo[] = [
      { id: 'gpt-5', label: 'GPT-5', multiplier: 3, vendor: 'openai', isDefault: true, thinkingLevels: ['minimal', 'low', 'medium', 'high'], defaultThinkingLevel: 'medium' },
      { id: 'gpt-5-mini', label: 'GPT-5 mini', multiplier: 1, vendor: 'openai', isDefault: false, thinkingLevels: ['minimal', 'low', 'medium', 'high'], defaultThinkingLevel: 'medium' },
    ];
    TestBed.configureTestingModule({
      imports: [CliModelSelectorComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getCliModelCatalog: (cli: string) => of({
              models: cli === 'codex' ? codexModels : claudeModels,
              source: 'test',
              fetchedAt: '2026-05-29T00:00:00Z',
            }),
          },
        },
      ],
    }).compileComponents();

    const store = TestBed.inject(CliCatalogStore);
    store.hydrateAll();

    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const commits: { cliType: string; model: string; thinkingLevel: string | null }[] = [];
    const cliChanges: string[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));
    fixture.componentInstance.cliTypeChange.subscribe((t) => cliChanges.push(t));

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onCliPillClick('codex');
    fixture.componentInstance.onModelPillClick('gpt-5-mini');

    expect(commits).toEqual([]);
    expect(fixture.componentInstance.pickerOpen()).toBe(true);

    fixture.componentInstance.onDoneClick();
    expect(commits).toEqual([{ cliType: 'codex', model: 'gpt-5-mini', thinkingLevel: 'medium' }]);
    expect(cliChanges).toEqual(['codex']);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });

  it('Done with no change is a no-op', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const commits: { cliType: string; model: string; thinkingLevel: string | null }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onDoneClick();
    expect(commits.length).toBe(0);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });

  it('uses the cached catalog synchronously when opening on a hydrated CLI', async () => {
    const codexModels: CliModelInfo[] = [
      { id: 'gpt-5', label: 'GPT-5', multiplier: 3, vendor: 'openai', isDefault: true, thinkingLevels: ['minimal', 'low', 'medium', 'high'], defaultThinkingLevel: 'medium' },
    ];
    TestBed.configureTestingModule({
      imports: [CliModelSelectorComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getCliModelCatalog: (cli: string) => of({
              models: cli === 'codex' ? codexModels : claudeModels,
              source: 'test',
              fetchedAt: '2026-05-29T00:00:00Z',
            }),
          },
        },
      ],
    }).compileComponents();

    const store = TestBed.inject(CliCatalogStore);
    store.hydrateAll();
    expect(store.hasFresh('claude')).toBe(true);
    expect(store.hasFresh('codex')).toBe(true);

    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    expect(fixture.componentInstance.draftAvailableModels()).toEqual(claudeModels);
    expect(fixture.componentInstance.loadingCatalog()).toBe(false);

    fixture.componentInstance.onCliPillClick('codex');
    expect(fixture.componentInstance.draftCliType()).toBe('codex');
    expect(fixture.componentInstance.draftAvailableModels()).toEqual(codexModels);
    expect(fixture.componentInstance.loadingCatalog()).toBe(false);
  });

  it('silently refreshes a stale Codex catalog on open so gpt-5.5 exposes xhigh', async () => {
    const staleCodexModels: CliModelInfo[] = [
      { id: 'gpt-5.5', label: 'GPT-5.5', multiplier: null, vendor: 'openai', isDefault: true, thinkingLevels: ['minimal', 'low', 'medium', 'high'], defaultThinkingLevel: 'medium' },
      { id: 'gpt-5.4', label: 'GPT-5.4', multiplier: null, vendor: 'openai', isDefault: false, thinkingLevels: ['minimal', 'low', 'medium', 'high'], defaultThinkingLevel: 'medium' },
      { id: 'gpt-5-codex', label: 'GPT-5 Codex', multiplier: null, vendor: 'openai', isDefault: false, thinkingLevels: ['minimal', 'low', 'medium', 'high'], defaultThinkingLevel: 'medium' },
    ];
    const freshCodexModels: CliModelInfo[] = [
      { ...staleCodexModels[0], thinkingLevels: ['minimal', 'low', 'medium', 'high', 'xhigh'] },
      staleCodexModels[1],
      staleCodexModels[2],
    ];
    const calls: { cli: string; refresh?: boolean }[] = [];

    TestBed.configureTestingModule({
      imports: [CliModelSelectorComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getCliModelCatalog: (cli: string, refresh?: boolean) => {
              calls.push({ cli, refresh });
              return of({
                models: cli === 'codex'
                  ? (refresh ? freshCodexModels : staleCodexModels)
                  : claudeModels,
                source: 'test',
                fetchedAt: '2026-06-09T00:00:00Z',
              });
            },
          },
        },
      ],
    }).compileComponents();

    const store = TestBed.inject(CliCatalogStore);
    store.hydrateAll();

    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'codex');
    fixture.componentRef.setInput('model', 'gpt-5.5');
    fixture.componentRef.setInput('thinkingLevel', 'high');
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.openPicker(new MouseEvent('click'));

    expect(calls).toContainEqual({ cli: 'codex', refresh: true });
    expect(fixture.componentInstance.draftThinkingLevels()).toEqual(['minimal', 'low', 'medium', 'high', 'xhigh']);
    expect(fixture.componentInstance.draftThinkingLevel()).toBe('high');

    fixture.componentInstance.onThinkingLevelPillClick('xhigh');
    expect(fixture.componentInstance.draftThinkingLevel()).toBe('xhigh');
    expect(fixture.componentInstance.draftThinkingLevels()).toContain('xhigh');

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillClick('gpt-5.4');
    expect(fixture.componentInstance.draftThinkingLevels()).toEqual(['minimal', 'low', 'medium', 'high']);

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillClick('gpt-5-codex');
    expect(fixture.componentInstance.draftThinkingLevels()).toEqual(['minimal', 'low', 'medium', 'high']);
  });

  it('exposes thinking levels only for capable models and resets on model change', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('thinkingLevel', 'xhigh');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    expect(fixture.componentInstance.draftThinkingLevels()).toEqual(['low', 'medium', 'high', 'xhigh', 'max']);
    expect(fixture.componentInstance.draftThinkingLevel()).toBe('xhigh');

    fixture.componentInstance.onModelPillClick('claude-sonnet-4-6');
    expect(fixture.componentInstance.draftThinkingLevels()).toEqual(['low', 'medium', 'high']);
    expect(fixture.componentInstance.draftThinkingLevel()).toBe('high');

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillClick('claude-haiku-4-5');
    expect(fixture.componentInstance.draftThinkingLevels()).toEqual([]);
    expect(fixture.componentInstance.draftThinkingLevel()).toBeNull();
  });

  it('uses the same trigger and picker contract for chat and code-review call sites', async () => {
    await configure();

    const cases = [
      { trigger: 'chat-compose-model', picker: 'chat-model-picker' },
      { trigger: 'code-review-model', picker: 'code-review-model-picker' },
    ];

    for (const c of cases) {
      const fixture = TestBed.createComponent(CliModelSelectorComponent);
      fixture.componentRef.setInput('cliType', 'claude');
      fixture.componentRef.setInput('model', 'claude-opus-4-7');
      fixture.componentRef.setInput('availableModels', claudeModels);
      fixture.componentRef.setInput('triggerTestid', c.trigger);
      fixture.componentRef.setInput('pickerTestidPrefix', c.picker);
      fixture.detectChanges();

      const trigger = fixture.nativeElement.querySelector(`[data-testid="${c.trigger}"]`) as HTMLButtonElement | null;
      expect(trigger).not.toBeNull();

      fixture.componentInstance.openPicker(new MouseEvent('click'));
      fixture.detectChanges();

      expect(fixture.componentInstance.pickerOpen()).toBe(true);
      expect(document.body.querySelector(`[data-testid="${c.picker}"]`)).not.toBeNull();
      expect(document.body.querySelector(`[data-testid="${c.picker}-cli-codex"]`)).not.toBeNull();
      expect(document.body.querySelector(`[data-testid="${c.picker}-model-claude-sonnet-4-6"]`)).not.toBeNull();
      fixture.componentInstance.closePicker();
      fixture.destroy();
    }
  });

  it('supports arrow-key selection inside radio groups', async () => {
    await configure();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    fixture.detectChanges();

    const commits: { cliType: string; model: string; thinkingLevel: string | null }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillKeydown('claude-opus-4-7', new KeyboardEvent('keydown', { key: 'ArrowDown' }));

    expect(commits).toEqual([{ cliType: 'claude', model: 'claude-sonnet-4-6', thinkingLevel: 'high' }]);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });
});
