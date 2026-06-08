import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  PromptAdminService,
  PromptCatalogItem,
  PromptDetail,
} from '../../../../services/prompt-admin.service';

interface PromptGroup {
  name: string;
  items: PromptCatalogItem[];
}

/**
 * Admin surface for the application-wide system prompts (the
 * `prompts/runtime/*.md` templates). Lists every template with a description
 * of what it steers, shows the shipped default vs the user override, and lets
 * the operator edit (override), reset to default, or re-baseline an override
 * against a changed default. Defaults are never mutated; edits land in a
 * user-data override directory that survives app updates.
 */
@Component({
  selector: 'app-prompt-admin-panel',
  standalone: true,
  imports: [FormsModule, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-admin-panel.component.html',
  styleUrl: './prompt-admin-panel.component.scss',
})
export class PromptAdminPanelComponent implements OnInit {
  private readonly api = inject(PromptAdminService);
  readonly catalog = this.api.catalog;
  readonly loadError = this.api.loadError;

  readonly selectedName = signal<string | null>(null);
  readonly detail = signal<PromptDetail | null>(null);
  readonly draft = signal<string>('');
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  /** Toggles the read-only "shipped default" comparison block. */
  readonly showDefault = signal(false);

  readonly groups = computed<PromptGroup[]>(() => {
    const cat = this.catalog();
    if (!cat) return [];
    const buckets = new Map<string, PromptCatalogItem[]>();
    for (const item of cat.items) {
      const list = buckets.get(item.group) ?? [];
      list.push(item);
      buckets.set(item.group, list);
    }
    // Backend already returns items in group + title order; preserve it.
    const seen: string[] = [];
    for (const item of cat.items) if (!seen.includes(item.group)) seen.push(item.group);
    return seen.map((name) => ({ name, items: buckets.get(name)! }));
  });

  readonly dirty = computed(() => {
    const d = this.detail();
    return d != null && this.draft() !== d.effectiveContent;
  });

  ngOnInit(): void {
    void this.init();
  }

  private async init(): Promise<void> {
    await this.api.loadCatalog();
    const first = this.catalog()?.items[0]?.name;
    if (first) await this.select(first);
  }

  async select(name: string): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.actionError.set(null);
    this.showDefault.set(false);
    try {
      const detail = await this.api.getDetail(name);
      this.selectedName.set(name);
      this.detail.set(detail);
      this.draft.set(detail.effectiveContent);
    } catch (err: unknown) {
      this.actionError.set(this.describe(err, 'Failed to load prompt'));
    } finally {
      this.busy.set(false);
    }
  }

  async save(): Promise<void> {
    const name = this.selectedName();
    if (!name || this.busy() || !this.dirty()) return;
    await this.run(() => this.api.saveOverride(name, this.draft()));
  }

  async resetToDefault(): Promise<void> {
    const name = this.selectedName();
    if (!name || this.busy()) return;
    await this.run(() => this.api.resetToDefault(name));
  }

  async takeNewDefault(): Promise<void> {
    // "Auf neuen Default": discard the override entirely so the shipped
    // default wins again.
    await this.resetToDefault();
  }

  async keepMine(): Promise<void> {
    // "Behalten": keep the override content, clear the drift banner by
    // re-baselining against the current default.
    const name = this.selectedName();
    if (!name || this.busy()) return;
    await this.run(() => this.api.rebaseline(name));
  }

  private async run(op: () => Promise<PromptDetail>): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const detail = await op();
      this.detail.set(detail);
      this.draft.set(detail.effectiveContent);
      await this.api.loadCatalog();
    } catch (err: unknown) {
      this.actionError.set(this.describe(err, 'Action failed'));
    } finally {
      this.busy.set(false);
    }
  }

  revertDraft(): void {
    const d = this.detail();
    if (d) this.draft.set(d.effectiveContent);
  }

  toggleDefault(): void {
    this.showDefault.update((v) => !v);
  }

  shortSha(sha: string | null): string {
    return sha ? sha.slice(0, 8) : '-';
  }

  private describe(err: unknown, fallback: string): string {
    if (err && typeof err === 'object') {
      const e = err as { error?: { error?: string }; message?: string };
      if (e.error?.error) return e.error.error;
      if (e.message) return e.message;
    }
    return fallback;
  }
}
