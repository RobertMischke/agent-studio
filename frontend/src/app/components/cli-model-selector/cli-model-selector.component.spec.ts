import { describe, expect, it, vi } from 'vitest';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';
import { CliModelSelectorComponent } from './cli-model-selector.component';
import { ModelSelectorComponent } from '@coding-agent/chat/composer';
import type { CliModelInfo } from '../../features/cli';
import { CliCatalogStore } from '../../services/cli-catalog.store';
import { ModalStackService } from '../../services/modal-stack.service';

/**
 * Adapter specs: the picker UI and its draft/commit semantics live in the
 * library's `<cac-model-selector>` (covered by the library's own specs).
 * These tests pin the app-side contract: the historical inputs/outputs and
 * testids survive, the catalog flows through `CliCatalogStore`, and the
 * popover participates in the app modal stack.
 */
describe('CliModelSelectorComponent (library adapter)', () => {
  const claudeModels: CliModelInfo[] = [
    { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'anthropic', isDefault: true, thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
    { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', multiplier: 1, vendor: 'anthropic', isDefault: false, thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
    { id: 'claude-retired', label: 'Retired', multiplier: null, vendor: 'anthropic', isDefault: false, available: false, deprecated: true },
  ];

  function createStoreMock() {
    return {
      hasFresh: vi.fn().mockReturnValue(true),
      modelsFor: vi.fn().mockReturnValue(claudeModels),
      ensure: vi.fn().mockReturnValue(of(claudeModels)),
      refresh: vi.fn().mockReturnValue(of(claudeModels)),
      refreshForPickerOpen: vi.fn().mockReturnValue(null),
    };
  }

  function createModalStackMock() {
    const dispose = vi.fn();
    return {
      dispose,
      service: { pushUntilDestroyed: vi.fn().mockReturnValue(dispose) },
    };
  }

  async function create(
    inputs: Record<string, unknown>,
    store = createStoreMock(),
    modalStack = createModalStackMock(),
  ): Promise<{
    fixture: ComponentFixture<CliModelSelectorComponent>;
    child: ModelSelectorComponent;
    store: ReturnType<typeof createStoreMock>;
    modalStack: ReturnType<typeof createModalStackMock>;
  }> {
    await TestBed.configureTestingModule({
      imports: [CliModelSelectorComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: CliCatalogStore, useValue: store },
        { provide: ModalStackService, useValue: modalStack.service },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    for (const [key, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(key, value);
    }
    await fixture.whenStable();
    const child = fixture.debugElement.query(By.directive(ModelSelectorComponent))
      .componentInstance as ModelSelectorComponent;
    return { fixture, child, store, modalStack };
  }

  function openPicker(fixture: ComponentFixture<CliModelSelectorComponent>): void {
    const trigger = fixture.nativeElement.querySelector('[data-testid="cli-model-selector-trigger"]') as HTMLButtonElement;
    trigger.click();
    fixture.detectChanges();
  }

  it('renders the legacy trigger testid with the short model label', async () => {
    const { fixture } = await create({ cliType: 'claude', model: 'claude-opus-4-7', availableModels: claudeModels });
    const chip = fixture.nativeElement.querySelector('[data-testid="cli-model-selector-trigger"]');
    expect(chip).toBeTruthy();
    expect(chip.textContent).toContain('opus 4.7');
  });

  it('serves a fresh catalog from the store and schedules the silent picker-open refresh', async () => {
    const { fixture, child, store } = await create({ cliType: 'claude', model: 'claude-opus-4-7' });
    openPicker(fixture);
    expect(store.modelsFor).toHaveBeenCalledWith('claude');
    expect(store.refreshForPickerOpen).toHaveBeenCalledWith('claude');
    await fixture.whenStable();
    // Unavailable entries are filtered by the library picker.
    expect(child.draftAvailableModels().map((m) => m.id)).toEqual(['claude-opus-4-7', 'claude-sonnet-4-6']);
  });

  it('loads via ensure() when the store has no fresh catalog', async () => {
    const store = createStoreMock();
    const pendingCatalog = new Subject<readonly CliModelInfo[]>();
    store.hasFresh.mockReturnValue(false);
    store.ensure.mockReturnValue(pendingCatalog);
    const { fixture, child } = await create({ cliType: 'claude', model: 'claude-opus-4-7' }, store);

    openPicker(fixture);
    expect(store.ensure).toHaveBeenCalledWith('claude');
    expect(fixture.componentInstance.catalogLoading()).toBe(true);

    pendingCatalog.next(claudeModels);
    pendingCatalog.complete();
    await fixture.whenStable();
    expect(fixture.componentInstance.catalogLoading()).toBe(false);
    expect(child.draftAvailableModels().length).toBe(2);
  });

  it('surfaces a catalog error when ensure() fails', async () => {
    const store = createStoreMock();
    store.hasFresh.mockReturnValue(false);
    store.ensure.mockReturnValue(throwError(() => new Error('boom')));
    const { fixture } = await create({ cliType: 'claude', model: 'claude-opus-4-7' }, store);

    openPicker(fixture);
    await fixture.whenStable();
    expect(fixture.componentInstance.catalogError()).toMatch(/could not load/i);
  });

  it('re-emits the library commit with the app CliType payload', async () => {
    const { fixture, child } = await create({ cliType: 'claude', model: 'claude-opus-4-7', thinkingLevel: 'high' });
    const commits: { cliType: string; model: string; thinkingLevel: string | null }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));
    const modelChanges: string[] = [];
    fixture.componentInstance.modelChange.subscribe((m) => modelChanges.push(m));

    openPicker(fixture);
    await fixture.whenStable();
    child.onModelPillClick('claude-sonnet-4-6');

    expect(commits).toEqual([
      { cliType: 'claude', model: 'claude-sonnet-4-6', thinkingLevel: 'high' },
    ]);
    expect(modelChanges).toEqual(['claude-sonnet-4-6']);
  });

  it('pushes onto the modal stack while open and disposes on close', async () => {
    const { fixture, child, modalStack } = await create({ cliType: 'claude', model: 'claude-opus-4-7' });
    openPicker(fixture);
    await fixture.whenStable();
    expect(modalStack.service.pushUntilDestroyed).toHaveBeenCalledTimes(1);

    child.closePicker();
    await fixture.whenStable();
    expect(modalStack.dispose).toHaveBeenCalledTimes(1);
  });

  it('surfaces a refresh error from the explicit Refresh affordance', async () => {
    const store = createStoreMock();
    store.refresh.mockReturnValue(throwError(() => new Error('boom')));
    const { fixture } = await create({ cliType: 'claude', model: 'claude-opus-4-7' }, store);

    openPicker(fixture);
    fixture.componentInstance.onRefreshRequested('claude');
    await fixture.whenStable();
    expect(fixture.componentInstance.catalogError()).toMatch(/could not refresh/i);
  });
});
