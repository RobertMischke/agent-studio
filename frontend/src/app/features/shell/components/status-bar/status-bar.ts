import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  ViewEncapsulation,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import {
  clearVisibleInterval,
  setVisibleInterval,
  type VisibleIntervalHandle,
} from '../../../../utils/visible-interval';
import { UsageHoverPanelComponent } from '../../../tokens';
import {
  deriveBoardRunningTruth,
  RemoteHostsService,
  ReviewQueueService,
} from '../../../remote-hosts';

import { StatusbarItemComponent } from '../statusbar-item/statusbar-item.component';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import {
  summarizeStatusBarHostLoad,
  summarizeStatusBarSlotsByRole,
  type StatusBarPlaneSlots,
} from './status-bar-host-load';
import { withRouteSegment } from '../../../../services/url-hash.util';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';
const STORAGE_DEFAULT_THINKING_PREFIX = 'defaultThinkingLevel:';
const HOST_LOAD_REFRESH_MS = 30_000;

export function formatSlotUtilizationLabel(
  plane: 'remote' | 'review',
  active: number | null,
  ceiling: number | null,
): string {
  if (active === null) return `${plane} unavailable`;
  if (active === 0) return `${plane} idle`;
  return ceiling === null ? `${plane} ${active} busy` : `${plane} ${active}/${ceiling}`;
}

export function formatPlaneHostDetail(
  plane: 'coding' | 'review',
  slots: StatusBarPlaneSlots,
): string {
  if (slots.hosts.length === 0) return `No connected remote ${plane} hosts.`;
  const count = slots.hosts.length;
  const hosts = slots.hosts.map(host => {
    const active = host.active === null ? '?' : host.active;
    const ceiling = host.ceiling === null ? '?' : host.ceiling;
    return `${host.name} (${active}/${ceiling} slots)`;
  }).join('; ');
  return `${count} ${count === 1 ? 'host' : 'hosts'} connected: ${hosts}.`;
}

@Component({
  selector: 'app-status-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [UsageHoverPanelComponent, StatusbarItemComponent, CliModelSelectorComponent],
  templateUrl: './status-bar.html',
  styleUrl: './status-bar.scss',
})
export class StatusBarComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(TaskService);
  private readonly clientDefaults = inject(ClientDefaultsService);
  private readonly remoteHosts = inject(RemoteHostsService);
  private readonly reviewQueue = inject(ReviewQueueService);
  private hostLoadRefreshHandle: VisibleIntervalHandle | null = null;

  readonly projectNames = input<string[]>([]);

  // Open-state of each overlay this bar can toggle. Bound to the panel's
  // own `xOpen` signal by the shell so the trigger button shows a
  // pressed/active state while its panel is visible (and `aria-pressed`
  // reflects it). The bar stays presentational — the source of truth for
  // "is the panel open" lives with the panel, not here.
  readonly usageOpen = input(false);
  readonly orchestratorOpen = input(false);
  readonly orchestratorActiveChatCount = input(0);
  readonly feedOpen = input(false);
  readonly settingsOpen = input(false);
  readonly showSignOut = input(false);
  readonly signedInLabel = input('');

  readonly toggleUsage = output<void>();
  readonly toggleOrchestrator = output<void>();
  readonly toggleFeed = output<void>();
  // Single entry into the global Workspace-settings home. Summary, visual
  // evidence and CLI management are sections of that home now, so the
  // status bar exposes one button instead of three scattered ones.
  readonly toggleSettings = output<void>();
  readonly signOut = output<void>();
  // Quota-strip click: open the CLI-Management (usage caps) section
  // directly, where the full usage detail lives. Separate from
  // `toggleSettings`, which lands on the home overview.
  readonly openCliAdmin = output<void>();
  readonly defaultCliChange = output<CliType>();
  readonly defaultModelChange = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();

  readonly defaultCli = signal<CliType>(this.readDefaultCli());
  readonly defaultModel = signal<string>(this.readDefaultModel(this.readDefaultCli()));
  readonly defaultThinkingLevel = signal<string | null>(this.readDefaultThinkingLevel(this.readDefaultCli()));

  readonly runningTruth = computed(() =>
    deriveBoardRunningTruth(this.jobService.grouped().progress));

  /**
   * Active execution slots and configured ceilings split by executor plane
   * (coding vs review, AGT-2645). Coding and review daemons register as
   * separate RunnerIds, so this is a read of the existing execution-host
   * registry, not a new data source.
   */
  readonly slotsByRole = computed(() => summarizeStatusBarSlotsByRole(this.remoteHosts.hosts()));

  readonly runningLabel = computed(() => {
    const coding = this.slotsByRole().coding;
    const remote = formatSlotUtilizationLabel('remote', coding.active, coding.ceiling);
    const local = this.runningTruth().local;
    return local > 0 ? `${local} local · ${remote}` : remote;
  });
  readonly remoteTelemetrySlots = computed(() => this.slotsByRole().coding.active);
  readonly runningSourcesDiverge = computed(() => {
    const telemetry = this.remoteTelemetrySlots();
    const boardRemote = this.runningTruth().remote;
    return telemetry !== null && telemetry !== boardRemote;
  });

  readonly executionActive = computed(() =>
    this.runningTruth().local > 0 || (this.slotsByRole().coding.active ?? 0) > 0);

  readonly hostLoad = computed(() =>
    summarizeStatusBarHostLoad(this.remoteHosts.hosts(), this.runningTruth().remote));

  readonly reviewSnapshot = computed(() => this.reviewQueue.snapshot());

  /** Active post-processing jobs (LLM orchestration workers running). */
  readonly reviewActiveJobs = computed(() => this.reviewSnapshot()?.activeJobs ?? 0);

  /** Cards waiting in the post-processing queue but not yet running. */
  readonly reviewWaiting = computed(() => this.reviewSnapshot()?.queueDepth ?? 0);

  /**
   * ATTENTION: cards are queued but nothing is draining. The operator sees an
   * amber signal so a silent stuck queue is never read as "idle and fine".
   */
  readonly reviewAttention = computed(() =>
    this.reviewWaiting() > 0 && this.reviewActiveJobs() === 0);

  /** Live review-daemon utilization; queue depth stays in the tooltip. */
  readonly reviewLabel = computed(() => {
    const review = this.slotsByRole().review;
    if (review.active === null
        && review.hosts.length === 0
        && this.reviewActiveJobs() === 0
        && this.reviewWaiting() === 0) {
      return 'review idle';
    }
    return formatSlotUtilizationLabel('review', review.active, review.ceiling);
  });

  /** Tooltip for the review plane item. */
  readonly reviewTooltip = computed(() => {
    const snap = this.reviewSnapshot();
    const hosts = formatPlaneHostDetail('review', this.slotsByRole().review);
    if (!snap) return `${hosts} Auto-review post-processing queue data is unavailable.`;
    const { activeJobs, queueDepth, isStagnant, stagnantThresholdMinutes } = snap;
    const base = `${hosts} Auto-review queue: ${activeJobs} processing, ${queueDepth} waiting.`;
    if (isStagnant) return `${base} Warning: queue has not drained for >${stagnantThresholdMinutes} minutes.`;
    return base;
  });

  readonly autoCount = computed(() => {
    const status = this.jobService.runnerStatus();
    return Object.values(status.projects).filter(
      p => p.mode === 'auto-continuous' || p.mode === 'auto-single'
    ).length;
  });

  readonly latestCliRepair = computed(() => {
    const repairs = this.jobService.runnerStatus().cliRepairs ?? [];
    return repairs.reduce<(typeof repairs)[number] | null>((latest, item) =>
      latest === null || Date.parse(item.occurredAt) > Date.parse(latest.occurredAt) ? item : latest,
    null);
  });

  readonly cliRepairLabel = computed(() => {
    const repair = this.latestCliRepair();
    if (!repair) return '';
    const parsed = Date.parse(repair.occurredAt);
    const time = Number.isFinite(parsed)
      ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })
        .format(new Date(parsed))
      : 'unknown time';
    return repair.outcome === 'repaired'
      ? `CLI repaired at ${time}`
      : `CLI repair failed at ${time}`;
  });

  readonly projectCount = computed(() => this.projectNames().length || Object.keys(this.jobService.runnerStatus().projects).length);
  readonly orchestratorLabel = computed(() => this.orchestratorActiveChatCount() > 0
    ? `Orchestrator · ${this.orchestratorActiveChatCount()} active`
    : 'Orchestrator');
  readonly orchestratorTooltip = computed(() => this.orchestratorActiveChatCount() > 0
    ? `${this.orchestratorActiveChatCount()} orchestrator chat(s) are working. Open Orchestrator Chat.`
    : 'Orchestrator chat');

  readonly activeFallback = computed(() => {
    for (const project of Object.values(this.jobService.runnerStatus().projects)) {
      if (project.activeJobId && project.quotaFallbackModel) return project;
    }
    return null;
  });

  ngOnInit(): void {
    this.remoteHosts.refresh();
    this.reviewQueue.refresh();
    this.hostLoadRefreshHandle = setVisibleInterval(
      () => { this.remoteHosts.refresh(); this.reviewQueue.refresh(); },
      HOST_LOAD_REFRESH_MS,
    );
    void this.clientDefaults.hydrate().then(() => {
      const cli = this.readDefaultCli();
      this.defaultCli.set(cli);
      this.defaultModel.set(this.readDefaultModel(cli));
      this.defaultThinkingLevel.set(this.readDefaultThinkingLevel(cli));
    });
  }

  ngOnDestroy(): void {
    clearVisibleInterval(this.hostLoadRefreshHandle);
  }

  runningTooltip(): string {
    const truth = this.runningTruth();
    const coding = this.slotsByRole().coding;
    const slotUse = coding.active === null
      ? 'Remote coding slot utilization is unavailable.'
      : coding.ceiling === null
        ? `Remote coding has ${coding.active} busy slots; the total is unavailable.`
        : `Remote coding uses ${coding.active}/${coding.ceiling} slots.`;
    const hosts = formatPlaneHostDetail('coding', coding);
    const execution = `Board leases report ${truth.local} local and ${truth.remote} remote ${truth.remote === 1 ? 'run' : 'runs'}.`;
    const telemetrySlots = this.remoteTelemetrySlots();
    const comparison = telemetrySlots === null
      ? ' Fresh remote slot telemetry is unavailable.'
      : this.runningSourcesDiverge()
        ? ` Warning: Board leases report ${truth.remote} remote, but fresh coding-host telemetry reports ${telemetrySlots} active slots.`
        : ` Board leases and host telemetry agree on ${truth.remote} remote ${truth.remote === 1 ? 'run' : 'runs'}.`;
    const load = this.hostLoad();
    if (!load) return `Open execution hosts. ${slotUse} ${hosts} ${execution}${comparison} Execution host load is unavailable.`;

    const loadDetail = `Execution host load ${load.load1.toFixed(1)} / ${load.cpuCores} cores `
      + `(${Math.round(load.ratio * 100)}%); ${load.activeSlots} active execution `
      + `${load.activeSlots === 1 ? 'slot' : 'slots'}.`;
    if (load.correlation === 'load-without-runs') {
      return `Open execution hosts. ${slotUse} ${hosts} ${execution}${comparison} ${loadDetail} Quiet consistency hint: host load is elevated without reported runs.`;
    }
    if (load.correlation === 'runs-without-load') {
      return `Open execution hosts. ${slotUse} ${hosts} ${execution}${comparison} ${loadDetail} Quiet consistency hint: reported runs and host load may not correspond.`;
    }
    return `Open execution hosts. ${slotUse} ${hosts} ${execution}${comparison} ${loadDetail}`;
  }

  autoTooltip(): string {
    return `${this.autoCount()} of ${this.projectCount()} project(s) have auto-pickup enabled.`;
  }

  navigateToExecutionHosts(): void {
    window.location.hash = withRouteSegment(
      window.location.hash,
      '/workspace/settings/execution-hosts',
    );
  }

  /** Atomic commit from the unified selector. Persists to localStorage and
   *  to the cross-device ClientDefaults profile; the create-task form
   *  subscribes via `defaultCliChange` / `defaultModelChange`. */
  onDefaultCommit(change: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    const previousCli = this.defaultCli();
    if (change.cliType !== previousCli) {
      this.defaultCli.set(change.cliType);
      localStorage.setItem(STORAGE_DEFAULT_CLI, change.cliType);
      void this.clientDefaults.pushDefaultCli(change.cliType);
      this.defaultCliChange.emit(change.cliType);
      // When the CLI flips we also need a fresh per-CLI model preference.
      this.defaultModel.set(this.readDefaultModel(change.cliType));
      this.defaultThinkingLevel.set(this.readDefaultThinkingLevel(change.cliType));
    }
    const model = change.model;
    const thinkingLevel = change.thinkingLevel;
    if (model !== this.defaultModel() || thinkingLevel !== this.defaultThinkingLevel() || change.cliType !== previousCli) {
      this.defaultModel.set(model);
      this.defaultThinkingLevel.set(thinkingLevel);
      if (model) {
        localStorage.setItem(STORAGE_DEFAULT_MODEL_PREFIX + change.cliType, model);
      } else {
        localStorage.removeItem(STORAGE_DEFAULT_MODEL_PREFIX + change.cliType);
      }
      if (thinkingLevel) {
        localStorage.setItem(STORAGE_DEFAULT_THINKING_PREFIX + change.cliType, thinkingLevel);
      } else {
        localStorage.removeItem(STORAGE_DEFAULT_THINKING_PREFIX + change.cliType);
      }
      void this.clientDefaults.pushDefaultModel(model);
      void this.clientDefaults.pushDefaultThinkingLevel(thinkingLevel);
      this.defaultModelChange.emit({ cliType: change.cliType, model, thinkingLevel });
    }
  }

  private readDefaultCli(): CliType {
    const stored = localStorage.getItem(STORAGE_DEFAULT_CLI) as CliType | null;
    if (stored && (CLI_TYPES as string[]).includes(stored)) return stored;
    return 'claude';
  }

  private readDefaultModel(cliType: CliType): string {
    return localStorage.getItem(STORAGE_DEFAULT_MODEL_PREFIX + cliType) ?? '';
  }

  private readDefaultThinkingLevel(cliType: CliType): string | null {
    return localStorage.getItem(STORAGE_DEFAULT_THINKING_PREFIX + cliType) ?? null;
  }
}
