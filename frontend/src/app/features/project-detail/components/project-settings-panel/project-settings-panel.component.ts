import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { ProjectDetailComponent } from '../project-detail/project-detail';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { TooltipDirective } from '../../../../components/tooltip';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import { QuotaApiService } from '../../../../features/quota';
import { WorkspaceOverlaysService } from '../../../shell';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';

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
 * orchestrator model, auto-commit / auto-push, lane sort, pipeline steps, CLI
 * permission modes) keep living in the embedded `<app-project-detail>` below,
 * so nothing the operator already relied on moves.
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

  /** Global default agent, read-only — the value the orchestrator inherits. */
  readonly defaultCli = signal<CliType | null>(null);
  readonly defaultModel = signal<string | null>(null);

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
    this.clientDefaults.getDefaults().subscribe({
      next: (r) => {
        const cli = r?.defaultCliType;
        if (cli && (CLI_TYPES as string[]).includes(cli)) {
          this.defaultCli.set(cli as CliType);
          this.defaultModel.set(r?.defaultModel ?? this.readStoredModel(cli as CliType));
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
  }

  private readStoredCli(): CliType | null {
    const stored = localStorage.getItem(STORAGE_DEFAULT_CLI);
    return stored && (CLI_TYPES as string[]).includes(stored) ? (stored as CliType) : null;
  }

  private readStoredModel(cli: CliType | null): string | null {
    if (!cli) return null;
    return localStorage.getItem(STORAGE_DEFAULT_MODEL_PREFIX + cli);
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
