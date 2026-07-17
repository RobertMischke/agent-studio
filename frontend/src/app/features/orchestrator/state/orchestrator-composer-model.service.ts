import { Injectable, computed, inject, signal } from '@angular/core';
import type { ChatModelControl, ChatModelSelection } from 'coding-agent-chat/core';
import { CliCatalogStore } from '../../../services/cli-catalog.store';
import type { CliModelInfo } from '../../cli';
import { availableCodexModels, resolveComposerSelection } from './orchestrator-composer-model.util';

/**
 * Workspace-persistent model choice for the canonical Orchestrator composer.
 * The operating mode is intentionally Codex-only; model inventory and
 * reasoning ladders remain owned by the live Codex catalogue.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorComposerModelService {
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly explicitSelection = signal<ChatModelSelection | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly catalog = computed<readonly CliModelInfo[]>(() => {
    return availableCodexModels(this.catalogStore.modelsFor('codex'));
  });

  readonly effectiveSelection = computed<ChatModelSelection>(() => {
    return resolveComposerSelection(
      this.catalog(),
      this.explicitSelection(),
      this.readPreference('defaultModel:codex'),
      this.readPreference('defaultThinkingLevel:codex'),
    );
  });

  readonly selectionSource = computed<'explicit' | 'inherited'>(() => {
    const explicit = this.explicitSelection();
    return explicit && this.catalog().some(model => model.id === explicit.model)
      ? 'explicit'
      : 'inherited';
  });

  readonly sourceLabel = computed(() => this.selectionSource() === 'explicit'
    ? 'Operator choice'
    : 'Inherited Codex default');

  readonly control = computed<ChatModelControl>(() => ({
    cliOptions: [{ id: 'codex', label: 'Codex', icon: '◆' }],
    cliType: 'codex',
    model: this.effectiveSelection().model,
    thinkingLevel: this.effectiveSelection().thinkingLevel,
    catalog: this.catalog(),
    catalogLoading: this.loading(),
    catalogError: this.error(),
  }));

  commit(selection: ChatModelSelection): void {
    if (selection.cliType !== 'codex') return;
    this.explicitSelection.set(resolveComposerSelection(this.catalog(), selection, null, null));
  }

  requestCatalog(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    const request = refresh
      ? this.catalogStore.refresh('codex')
      : this.catalogStore.ensure('codex');
    request.subscribe({
      next: () => this.loading.set(false),
      error: () => {
        this.loading.set(false);
        this.error.set('Could not load the Codex model catalogue.');
      },
    });
  }

  private readPreference(key: string): string | null {
    if (typeof window === 'undefined') return null;
    try {
      return window.localStorage?.getItem(key) ?? null;
    } catch {
      return null;
    }
  }
}
