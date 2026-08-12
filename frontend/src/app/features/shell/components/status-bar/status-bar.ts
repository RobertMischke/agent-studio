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
  freshExecutionPlaneSlots,
  freshRemoteTelemetrySlots,
  RemoteHostsService,
} from '../../../remote-hosts';

import { StatusbarItemComponent } from '../statusbar-item/statusbar-item.component';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { summarizeStatusBarHostLoad } from './status-bar-host-load';
import { withRouteSegment } from '../../../../services/url-hash.util';
import { ReviewQueueTelemetryStore } from '../../../../services/review-queue-telemetry.store';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';
const STORAGE_DEFAULT_THINKING_PREFIX = 'defaultThinkingLevel:';
const HOST_LOAD_REFRESH_MS = 30_000;

export function formatRunningLabel(
  local: number,
  remote: number,
  reviewActive = 0,
  reviewWaiting = 0,
): string {
  const parts: string[] = [];
  if (local > 0) parts.push(`${local} local`);
  if (remote > 0) parts.push(`${remote} remote`);
  if (reviewActive > 0 || reviewWaiting > 0) {
    parts.push(`review ${reviewActive} active / ${reviewWaiting} waiting`);
  }
  if (parts.length > 0) return parts.join(' · ');
  return 'no runners';
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
  private readonly reviewTelemetry = inject(ReviewQueueTelemetryStore);
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
  readonly runningCount = computed(() => this.runningTruth().total);
  readonly reviewQueue = this.reviewTelemetry.snapshot;
  readonly reviewSlots = computed(() => Math.max(
    freshExecutionPlaneSlots(this.remoteHosts.hosts(), 'review') ?? 0,
    this.reviewQueue()?.activeReviews ?? 0,
  ));
  readonly reviewWaiting = computed(() => this.reviewQueue()?.waitingDepth ?? 0);
  readonly reviewAttention = computed(() =>
    this.reviewWaiting() > 0 && this.reviewSlots() === 0);
  readonly totalActivityCount = computed(() => this.runningCount() + this.reviewSlots());
  readonly runningLabel = computed(() => {
    const truth = this.runningTruth();
    return formatRunningLabel(
      truth.local,
      truth.remote,
      this.reviewSlots(),
      this.reviewWaiting(),
    );
  });
  readonly remoteTelemetrySlots = computed(() =>
    freshRemoteTelemetrySlots(this.remoteHosts.hosts()));
  readonly runningSourcesDiverge = computed(() => {
    const telemetry = this.remoteTelemetrySlots();
    return telemetry !== null && telemetry !== this.runningTruth().remote;
  });

  readonly hostLoad = computed(() =>
    summarizeStatusBarHostLoad(this.remoteHosts.hosts(), this.runningTruth().remote));
  readonly runningSignalTone = computed(() =>
    this.reviewAttention() ? 'hot' : (this.hostLoad()?.tone ?? 'unknown'));
  readonly runningSignalCorrelation = computed(() =>
    this.reviewAttention()
      ? 'review-waiting-without-active'
      : (this.hostLoad()?.correlation ?? 'unknown'));

  readonly autoCount = computed(() => {
    const status = this.jobService.runnerStatus();
    return Object.values(status.projects).filter(
      p => p.mode === 'auto-continuous' || p.mode === 'auto-single'
    ).length;
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
    this.reviewTelemetry.refresh();
    this.hostLoadRefreshHandle = setVisibleInterval(
      () => {
        this.remoteHosts.refresh();
        this.reviewTelemetry.refresh();
      },
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
    const execution = `Coding runs ${truth.total} - ${truth.local} local / ${truth.remote} remote. `
      + `Review plane ${this.reviewSlots()} active ${this.reviewSlots() === 1 ? 'slot' : 'slots'} / `
      + `${this.reviewWaiting()} waiting.`;
    const attention = this.reviewAttention()
      ? ` Attention: ${this.reviewWaiting()} Auto Review ${this.reviewWaiting() === 1 ? 'card is' : 'cards are'} waiting while the Review plane reports 0 active slots. Consistency hint: waiting post-processing has no active Review worker.`
      : '';
    const telemetrySlots = this.remoteTelemetrySlots();
    const comparison = telemetrySlots === null
      ? ' Fresh remote slot telemetry is unavailable.'
      : this.runningSourcesDiverge()
        ? ` Warning: Board leases report ${truth.remote} remote, but fresh host telemetry reports ${telemetrySlots} active slots.`
        : ` Board leases and host telemetry agree on ${truth.remote} remote ${truth.remote === 1 ? 'run' : 'runs'}.`;
    const load = this.hostLoad();
    if (!load) return `Open execution hosts. ${execution}${attention}${comparison} Execution host load is unavailable.`;

    const loadDetail = `Execution host load ${load.load1.toFixed(1)} / ${load.cpuCores} cores `
      + `(${Math.round(load.ratio * 100)}%); ${load.activeSlots} active execution `
      + `${load.activeSlots === 1 ? 'slot' : 'slots'} `
      + `(${load.codingSlots} coding / ${load.reviewSlots} review).`;
    if (load.correlation === 'load-without-runs') {
      return `Open execution hosts. ${execution}${attention}${comparison} ${loadDetail} Quiet consistency hint: host load is elevated without reported runs.`;
    }
    if (load.correlation === 'runs-without-load') {
      return `Open execution hosts. ${execution}${attention}${comparison} ${loadDetail} Quiet consistency hint: reported runs and host load may not correspond.`;
    }
    return `Open execution hosts. ${execution}${attention}${comparison} ${loadDetail}`;
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
