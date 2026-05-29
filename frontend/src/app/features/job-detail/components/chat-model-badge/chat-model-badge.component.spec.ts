import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { ChatModelBadgeComponent } from './chat-model-badge.component';
import type { CliModelInfo } from '../../../cli';
import { CliCatalogStore } from '../../../../services/cli-catalog.store';
import { JobService } from '../../../../services/task.service';

/**
 * Behavioural specs for the chat-compose CLI + model picker. Covers the
 * regression that prompted the redesign: the dialog must stay open after a
 * CLI switch, the model list must refresh, and a clean Cancel path must not
 * commit. The pure formatting helpers (label, tooltip, short name) keep
 * their coverage in
 * `../protocol-pane/protocol-pane/model-badge-menu-builders.spec.ts`.
 */
describe('ChatModelBadgeComponent', () => {
  const claudeModels: CliModelInfo[] = [
    { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'anthropic', isDefault: true },
    { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', multiplier: 1, vendor: 'anthropic', isDefault: false },
  ];

  function configure() {
    return TestBed.configureTestingModule({
      imports: [ChatModelBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
  }

  it('renders the badge label from inputs and starts with the picker closed', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance.displayName()).toBe('opus 4.7');
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
    expect(fixture.componentInstance.disabledReason()).toBeNull();
  });

  it('reports a disabled reason while a run is in flight', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', true);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] disabled-render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance.disabledReason()).toMatch(/stop the run/i);
  });

  it('seeds the draft from inputs when the picker opens', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
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

  it('cancel after a CLI switch discards the draft and emits nothing', async () => {
    // Model-only clicks now auto-commit (the operator expectation: "click
    // model = persist"), so the cancel-discard semantics are exercised by
    // the CLI-switch flow instead - that flow keeps the picker open until
    // Done, which is where a Cancel can intercept a pending change.
    const codexModels: CliModelInfo[] = [
      { id: 'gpt-5', label: 'GPT-5', multiplier: 3, vendor: 'openai', isDefault: true },
    ];
    TestBed.configureTestingModule({
      imports: [ChatModelBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: JobService,
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

    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    let commits = 0;
    fixture.componentInstance.commit.subscribe(() => commits++);
    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onCliPillClick('codex');
    expect(fixture.componentInstance.pickerOpen()).toBe(true);
    expect(fixture.componentInstance.hasChanges()).toBe(true);

    fixture.componentInstance.onCancelClick();
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
    expect(commits).toBe(0);
  });

  it('clicking a model pill without a CLI change auto-commits + closes', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const commits: { cliType: string; model: string }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillClick('claude-sonnet-4-6');

    expect(commits).toEqual([{ cliType: 'claude', model: 'claude-sonnet-4-6' }]);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });

  it('after a CLI switch, model click keeps the picker open until Done', async () => {
    // Atomic-after-CLI-switch flow (f421f2d / ASS-532): once the user
    // touched the CLI pills, both the CLI and the model PUT must travel
    // together so the model PUT validates against the new CLI's catalog.
    // The picker therefore stays open after a model click in that case
    // and Done is what fires the single commit.
    const codexModels: CliModelInfo[] = [
      { id: 'gpt-5', label: 'GPT-5', multiplier: 3, vendor: 'openai', isDefault: true },
      { id: 'gpt-5-mini', label: 'GPT-5 mini', multiplier: 1, vendor: 'openai', isDefault: false },
    ];
    TestBed.configureTestingModule({
      imports: [ChatModelBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: JobService,
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

    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const commits: { cliType: string; model: string }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onCliPillClick('codex');
    fixture.componentInstance.onModelPillClick('gpt-5-mini');

    // No commit yet - the picker stays open waiting for Done.
    expect(commits).toEqual([]);
    expect(fixture.componentInstance.pickerOpen()).toBe(true);

    fixture.componentInstance.onDoneClick();
    expect(commits).toEqual([{ cliType: 'codex', model: 'gpt-5-mini' }]);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });

  it('uses the cached catalog synchronously when opening on a hydrated CLI (ADR-0046)', async () => {
    // Stub the JobService so the store can be hydrated without HTTP.
    const codexModels: CliModelInfo[] = [
      { id: 'gpt-5', label: 'GPT-5', multiplier: 3, vendor: 'openai', isDefault: true },
    ];
    TestBed.configureTestingModule({
      imports: [ChatModelBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: JobService,
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

    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    // Pass an EMPTY availableModels input so the only way the picker
    // can render Claude's catalogue is via the store cache.
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    expect(fixture.componentInstance.pickerOpen()).toBe(true);
    expect(fixture.componentInstance.draftAvailableModels()).toEqual(claudeModels);
    expect(fixture.componentInstance.loadingCatalog()).toBe(false);

    // Switching CLI inside the open picker also resolves from cache - no spinner.
    fixture.componentInstance.onCliPillClick('codex');
    expect(fixture.componentInstance.draftCliType()).toBe('codex');
    expect(fixture.componentInstance.draftAvailableModels()).toEqual(codexModels);
    expect(fixture.componentInstance.loadingCatalog()).toBe(false);
  });

  it('Done with no change is a no-op', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const commits: { cliType: string; model: string }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onDoneClick();
    expect(commits.length).toBe(0);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });
});
