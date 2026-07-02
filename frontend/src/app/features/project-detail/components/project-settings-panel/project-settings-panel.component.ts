import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ProjectDetailComponent } from '../project-detail/project-detail';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { TooltipDirective } from '@coding-agent/chat/shared';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import { QuotaApiService } from '../../../../features/quota';
import { WorkspaceOverlaysService } from '../../../shell';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';
const STORAGE_DEFAULT_THINKING_PREFIX = 'defaultThinkingLevel:';

interface CapSummaryRow {
  cliType: CliType;
  label: string;
  icon: string;
  windows: { windowLabel: string; capPct: number }[];
}

/**
 * Project-level Settings panel. Mirrors the global Workspace-settings home
 * ("Dach"): a header + a "Workspace defaults" card section that surfaces the
 * global default agent (CLI + model) and the per-CLI usage caps, each labelled
 * as inherited from the global Workspace settings.
 *
 * Neither of those two defaults has a per-project override backend today, so
 * they render read-only with a deep-link affordance into the matching global
 * Workspace-settings section (`overview` for the default agent, `caps` for the
 * usage caps). The per-project settings that DO override globals (runner mode,
 * orchestrator model, auto-commit / auto-push) keep living in the embedded
 * `<app-project-detail view="settings">` below.
 *
 * Nav-rebuild step 2 (T5b) relocated three formerly-embedded sections to their
 * own project rails — lane sort → Workflow, pipeline steps → Pipeline, CLI
 * permission modes → workspace Admin / CLI & Modelle — so Settings shrinks to
 * the rest. The controls are unchanged (Funktions-Diff = 0); only the mount
 * location moved.
 */
@Component({
  selector: 'app-project-settings-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ProjectDetailComponent, CliModelSelectorComponent, TooltipDirective],
  templateUrl: './project-settings-panel.component.html',
  styleUrl: './project-settings-panel.component.scss',
})
export class ProjectSettingsPanelComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly clientDefaults = inject(ClientDefaultsService);
  private readonly quotaApi = inject(QuotaApiService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly http = inject(HttpClient);

  /** ADR-0052: per-project max parallel coding slots (1 = sequential). */
  readonly maxParallelism = signal<number>(1);
  readonly parallelOptions = [1, 2, 3, 4];

  /** Global default agent, read-only — the value the orchestrator inherits. */
  readonly defaultCli = signal<CliType | null>(null);
  readonly defaultModel = signal<string | null>(null);
  readonly defaultThinkingLevel = signal<string | null>(null);

  /** Global per-CLI usage caps, read-only. */
  readonly defaultCapPct = signal<number>(95);
  private readonly caps = signal<Record<string, Record<string, number>>>({});

  readonly capRows = computed<CapSummaryRow[]>(() => {
    const caps = this.caps();
    const out: CapSummaryRow[] = [];
    for (const cli of Object.keys(caps) as CliType[]) {
      const windows = Object.entries(caps[cli] ?? {})
        .filter(([label]) => !!label)
        .map(([windowLabel, capPct]) => ({ windowLabel, capPct }));
      if (windows.length === 0) continue;
      out.push({
        cliType: cli,
        label: cliTypeLabel(cli),
        icon: cliTypeIcon(cli),
        windows,
      });
    }
    return out;
  });

  ngOnInit(): void {
    // Seed from the localStorage cache the status bar writes so the chip
    // shows a sensible value immediately, then reconcile with the durable
    // backend value (the one the orchestrator actually inherits).
    this.defaultCli.set(this.readStoredCli());
    this.defaultModel.set(this.readStoredModel(this.readStoredCli()));
    this.defaultThinkingLevel.set(this.readStoredThinkingLevel(this.readStoredCli()));
    this.clientDefaults.getDefaults().subscribe({
      next: (r) => {
        const cli = r?.defaultCliType;
        if (cli && (CLI_TYPES as string[]).includes(cli)) {
          this.defaultCli.set(cli as CliType);
          this.defaultModel.set(r?.defaultModel ?? this.readStoredModel(cli as CliType));
          this.defaultThinkingLevel.set(r?.defaultThinkingLevel ?? this.readStoredThinkingLevel(cli as CliType));
        }
      },
      error: () => { /* keep the localStorage-seeded value */ },
    });
    this.quotaApi.getQuotaCaps().subscribe({
      next: (resp) => {
        this.defaultCapPct.set(resp.defaultCapPct);
        this.caps.set(resp.caps ?? {});
      },
      error: () => { /* keep the default-cap fallback */ },
    });
    this.http.get<Record<string, { maxParallelism?: number }>>('/api/projects/settings').subscribe({
      next: (all) => {
        const n = all?.[this.projectName()]?.maxParallelism;
        if (typeof n === 'number' && n >= 1) this.maxParallelism.set(n);
      },
      error: () => { /* default 1 */ },
    });
  }

  /** Persist the per-project max parallelism (ADR-0052; PUT /api/projects/{name}/max-parallelism). */
  setMaxParallelism(n: number): void {
    const v = Math.max(1, Math.floor(n || 1));
    this.maxParallelism.set(v);
    this.http
      .put(`/api/projects/${encodeURIComponent(this.projectName())}/max-parallelism`, { maxParallelism: v })
      .subscribe({ next: () => { /* applied live */ }, error: () => { /* surfaced on next load */ } });
  }

  private readStoredCli(): CliType | null {
    const stored = localStorage.getItem(STORAGE_DEFAULT_CLI);
    return stored && (CLI_TYPES as string[]).includes(stored) ? (stored as CliType) : null;
  }

  private readStoredModel(cli: CliType | null): string | null {
    if (!cli) return null;
    return localStorage.getItem(STORAGE_DEFAULT_MODEL_PREFIX + cli);
  }

  private readStoredThinkingLevel(cli: CliType | null): string | null {
    if (!cli) return null;
    return localStorage.getItem(STORAGE_DEFAULT_THINKING_PREFIX + cli);
  }

  /** Open the global Workspace-settings home (where the default agent lives). */
  openWorkspaceSettings(): void {
    this.overlays.openSettings();
  }

  /** Open the global Workspace-settings usage-caps section directly. */
  openUsageCaps(): void {
    this.overlays.openCliAdmin();
  }
}
