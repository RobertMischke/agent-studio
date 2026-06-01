import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import type { AutoLoopSnapshot, TaskInfo, PendingIntent, EpicRollup } from '../../../../models/task.model';
import { GitSummaryService } from '../../../../services/git-summary.service';
import { TaskService } from '../../../../services/task.service';
import { ClientService } from '../../../../services/client.service';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';
import { CodeReviewActivityStore } from '../../../../services/code-review-activity.store';
import { cliTypeIcon } from '../../../../services/format.util';
import { projectIdentity } from '../../../../services/project-identity.util';
import { TagRegistryStore } from '../../../../services/tag-registry.store';
import { shouldShowFailureToast } from '../../../task-detail/services/run-outcome.util';
import {
  buildCommitChainTooltip,
  buildCommitChainView,
  buildCommitEmptyBadge,
  buildEffectiveModelChip,
  buildTaskTypeChip,
  buildTokenBubble,
  commitChainVariant,
  formatShortTime,
  formatTokens,
  type CommitChainView,
  type CommitEmptyBadge,
} from './task-card-view-model';

import { TooltipDirective } from '../../../../components/tooltip';
import { TaskStatusPopoverDirective } from '../../../../components/task-status-card';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../../../../components/menu';
import { TokenPopoverDirective } from './token-popover.directive';
import { NotificationService } from '../../../../services/notification.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { BoardFiltersService } from '../../state/board-filters.service';
// Shared 'now' signal that ticks every 30s so all relative timestamps update in lockstep
// without re-reading Date.now() during change detection (which causes NG0100).
const nowTick = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => nowTick.set(Date.now()), 30_000);
}

@Component({
  selector: 'app-task-card, app-job-card',
  standalone: true,
  imports: [TooltipDirective, TaskStatusPopoverDirective, MenuComponent, TokenPopoverDirective],
  // OnPush + signal-based reactivity. With ~30+ cards in a single
  // 4-auto-review lane, default Zone CD on every microtask was cumulating
  // into 80-100 ms long tasks during scroll/poll bursts. The component's
  // template only reads signal inputs, computed signals, and the shared
  // `nowTick` signal, so OnPush updates remain correct without any
  // explicit `markForCheck` calls.
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-card.component.html',
  styleUrl: './task-card.component.scss',
})
export class TaskCardComponent implements OnInit, OnDestroy {
  readonly job = input.required<TaskInfo>();
  readonly compact = input<boolean>(false);
  /**
   * F2: when set and matches this card's job id, the card renders the
   * "just created" pulse highlight and scrolls itself into view on the
   * board. The host clears the signal after one animation cycle.
   */
  readonly highlightJobId = input<string | null>(null);
  readonly deleteRequested = output<TaskInfo>();
  /**
   * F5: emitted when the user clicks the inline "Pick next" affordance
   * on a 2-ready card. The host wires this to `moveJobToTop` so the
   * runner picks it up on the next cycle.
   */
  readonly pickNextRequested = output<TaskInfo>();
  private readonly hostRef = inject(ElementRef<HTMLElement>);

  /** True when this card should render the just-created highlight. */
  readonly isJustCreated = computed(() => this.highlightJobId() === this.job().id);

  /**
   * Scroll-into-view effect: when this card becomes the highlighted
   * "just created" target, scroll it into the board viewport so the
   * operator's eye lands on it even on a 200+ card board.
   */
  private readonly scrollEffect = effect(() => {
    if (!this.isJustCreated()) return;
    queueMicrotask(() => {
      try {
        this.hostRef.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
      } catch { /* SSR / detached DOM — ignore */ }
    });
  });
  private readonly gitSummary = inject(GitSummaryService);
  private readonly clients = inject(ClientService);
  private readonly tagRegistry = inject(TagRegistryStore);
  private readonly autoReviewStatus = inject(AutoReviewStatusStore);
  private readonly codeReviewActivity = inject(CodeReviewActivityStore);
  private stopPolling: (() => void) | null = null;

  /**
   * Backlog-lane spec: render the structural classification as a small
   * chip (🐞 Bug / ✨ Feature / · Chore). The chip is always visible — even
   * for the default `chore` value — so the user can scan a lane for
   * features vs technical work without filtering. Legacy `user-story`
   * values render as Feature so cards predating the rename keep a stable
   * chip.
   */
  readonly taskTypeChip = computed(() => buildTaskTypeChip(this.job().taskType));

  /**
   * Tag chips on the card. Looks up label + colour from the workspace
   * registry signal; tags whose id no longer exists in the registry render
   * as a faint "ghost" chip with the raw id so the user knows to clean up.
   */
  readonly tagChips = computed(() => {
    const ids = this.job().tags ?? [];
    if (ids.length === 0) return [];
    const byId = this.tagRegistry.byId();
    return ids.map(id => {
      // A1 (2026-05-21): review:unparseable is a structurally different
      // signal from {namespace}:concerns — the model didn't follow the
      // verdict format, NOT a real quality concern. Render it as its own
      // chip variant so the operator can sort/scan past "format
      // violations" without conflating them with model-flagged issues.
      if (id === 'review:unparseable') {
        return {
          id,
          label: 'review:unparseable',
          color: '#a5b4fc',
          ghost: false,
          concern: false,
          unparseable: true,
          tooltip: 'Auto-review could not parse the model\'s verdict (no [[ASPECT_VERDICT]] sentinel). NOT a quality concern; the model just did not follow the format. See aspect-*.md for the raw reply.'
        };
      }
      // Auto-review concern tags use the `<namespace>:concerns` shape and
      // are not in the registry by design (they are ephemeral findings,
      // not curated taxonomy). The card renders them with a small ⚠ chip
      // so the user sees the source aspect at a glance instead of a
      // generic "unknown tag" ghost. See ADR-0025.
      const concernMatch = /^([a-z][a-z0-9-]*):concerns$/i.exec(id);
      if (concernMatch) {
        const ns = concernMatch[1];
        return {
          id,
          label: `${ns}:concerns`,
          color: '#fbbf24',
          ghost: false,
          concern: true,
          unparseable: false,
          tooltip: `Auto-review aspect '${ns}' flagged concerns. Open the task and read aspect-*.md for details.`
        };
      }
      const entry = byId.get(id);
      if (entry) {
        return {
          id,
          label: entry.label,
          color: entry.color,
          ghost: false,
          concern: false,
          unparseable: false,
          tooltip: entry.description ? `${entry.label}: ${entry.description}` : entry.label
        };
      }
      return {
        id,
        label: id,
        color: '#475569',
        ghost: true,
        concern: false,
        unparseable: false,
        tooltip: `Unknown tag '${id}'; registry entry was removed`
      };
    });
  });

  onDeleteClick(event: Event) {
    event.stopPropagation();
    this.deleteRequested.emit(this.job());
  }

  /** True for cards where "Pick next" makes sense (front-of-queue promotion). */
  readonly canPickNext = computed(() => this.job().state === '2-ready');

  onPickNextClick(event: Event) {
    event.stopPropagation();
    this.pickNextRequested.emit(this.job());
  }

  /**
   * Owner-attribution chip on every card. Resolves the job's
   * `ownerClientId` against the registry from /api/clients and renders
   * emoji + display name + the owner's chosen colour. Falls back to a
   * neutral placeholder when the registry has not loaded yet.
   */
  readonly ownerChip = computed<{
    id: string;
    label: string;
    emoji: string;
    background: string;
    border: string;
    foreground: string;
    tooltip: string;
  } | null>(() => {
    const ownerId = this.job().ownerClientId;
    if (!ownerId) return null;
    const c = this.clients.resolve(ownerId);
    const baseColour = c.colour || '#64748b';
    return {
      id: c.id,
      label: c.displayName || c.id,
      emoji: c.emoji || '·',
      background: this.tintFromHex(baseColour, 0.12),
      border: this.tintFromHex(baseColour, 0.32),
      foreground: '#e2e8f0',
      tooltip: `Owner: ${c.displayName || c.id} (${c.id})`
    };
  });

  private tintFromHex(hex: string, alpha: number): string {
    const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
    if (!m) return `rgba(100,116,139,${alpha})`;
    let body = m[1];
    if (body.length === 3) body = body.split('').map(ch => ch + ch).join('');
    const r = parseInt(body.slice(0, 2), 16);
    const g = parseInt(body.slice(2, 4), 16);
    const b = parseInt(body.slice(4, 6), 16);
    return `rgba(${r},${g},${b},${alpha})`;
  }

  // Live working-tree state (branch + uncommitted file count) only makes
  // sense while the agent is actively touching the repo. In review lanes
  // the task is "frozen" against a specific commit and live state would
  // misrepresent it (the board's project status is shared across cards;
  // a card sitting in 5-human-review must not advertise the dev branch
  // someone else just switched to). Pre-work and post-review lanes carry
  // no useful per-task git context either, so the pill is suppressed
  // everywhere except 3-progress.
  private static readonly LANES_WITH_GIT = new Set([
    '3-progress',
  ]);

  readonly gitPill = computed(() => {
    if (!TaskCardComponent.LANES_WITH_GIT.has(this.job().state)) return null;
    const projectName = this.job().projectName;
    const summary = this.gitSummary.value().find(s => s.projectName === projectName);
    return summary && summary.isRepo ? summary : null;
  });

  /**
   * Commit-chain view model (AC#1/#4). Reads the attributed `commits[]`
   * chain (single source of truth), falling back to the legacy singular
   * `commit` only when `commits[]` is absent. Never sources commit data
   * from repo HEAD / the working tree - that was bug (1) ("main: 20 files"
   * leaking into review lanes). Renders newest-first, up to three rows,
   * then a "+N more" disclosure. `full` (3-progress) prefixes each row with
   * the ⏺ SHA so the working agent can correlate it with its own
   * auto-commit; `review` keeps the SHA but drops the glyph. Returns null
   * outside the commit-pill lanes or when the chain is empty (the 0-commit
   * case is handled by {@link commitEmptyBadge} instead).
   */
  readonly commitChainView = computed<CommitChainView | null>(() => {
    const variant = commitChainVariant(this.job().state);
    if (!variant) return null;
    return buildCommitChainView(this.job(), variant);
  });

  /**
   * Zero-commit diagnostic for review-lane cards (AC#3, bug (3)). See
   * {@link buildCommitEmptyBadge}: only fires in review lanes when the
   * attributed chain is empty, and uses the scanner's `codeActivityDetected`
   * signal (never repo HEAD) to separate an analysis-only no-op ("no code
   * changes") from a run that moved HEAD without an attributed commit
   * ("commit discovery pending").
   */
  readonly commitEmptyBadge = computed<CommitEmptyBadge | null>(() => buildCommitEmptyBadge(this.job()));

  readonly gitTooltip = computed(() => {
    const g = this.gitPill();
    if (!g) return '';
    return `Branch: ${g.branch ?? '(detached)'}\n${g.filesChanged} changed file(s) in ${g.rootPath}\n+${g.totalAdded} / −${g.totalRemoved}`;
  });

  /**
   * Commit-chain tooltip. A single commit lists the files it touched; a
   * multi-commit chain lists every SHA with its subject and rolled-up file
   * total so a card carrying auto-review concerns makes the affected scope
   * visible without opening the job. HTML escaping is handled by the tooltip
   * controller's DOMPurify pass.
   */
  readonly commitTooltip = computed(() => buildCommitChainTooltip(this.job()));

  ngOnInit(): void { this.stopPolling = this.gitSummary.ensurePolling(); }
  ngOnDestroy(): void { this.stopPolling?.(); }

  stateLabel(): string {
    const state = this.job().state;
    const name = state.includes('-') ? state.substring(state.indexOf('-') + 1) : state;
    return name.replace(/-/g, ' ');
  }

  /**
   * Lifecycle-phase chip. Surfaces the `phase` substate on cards that
   * carry one (Ready group: human-ready / intake-running / intake-blocked /
   * intake-passed). Hidden when the job has no explicit phase, so cards
   * that predate the field render exactly like before.
   */
  phaseBadge(): { label: string; tone: 'human-ready' | 'intake-running' | 'intake-blocked' | 'intake-passed'; tooltip: string } | null {
    const phase = this.job().phase ?? null;
    if (!phase) return null;
    switch (phase) {
      case 'human-ready':
        return { label: 'Ready', tone: 'human-ready',
                 tooltip: 'The user marked this task ready. Orchestrator intake will check it before the coding runner picks it up.' };
      case 'intake-running':
        return { label: 'Intake running', tone: 'intake-running',
                 tooltip: 'Orchestrator intake is checking this card (separate runner from the coding CLI).' };
      case 'intake-blocked':
        return { label: 'Intake blocked', tone: 'intake-blocked',
                 tooltip: 'Orchestrator intake flagged this card. Check the activity log for the reason and resolve before the coding runner can pick it up.' };
      case 'intake-passed':
        return { label: 'Intake passed', tone: 'intake-passed',
                 tooltip: 'Orchestrator intake approved this card. The coding runner is now allowed to pick it up.' };
      default:
        return null;
    }
  }

  executionBadge(): { label: string; tone: 'running' | 'failed' | 'cancelled' } | null {
    const execution = this.job().execution;
    if (!execution) return null;

    // Lane wins over execution-status. The backend overlay already clears
    // Execution for non-progress tasks (TaskEndpointHelpers.WithRuntime), but
    // a stale poll snapshot or an optimistic move can briefly land on the
    // card before the next round-trip. Without this guard, a card in
    // 4-auto-review / 5-human-review can flash "Running live" while the
    // task is not actually executing in this lane.
    if (this.job().state !== '3-progress') return null;

    if (execution.status === 'running') {
      return { label: 'Running live', tone: 'running' };
    }

    if (shouldShowFailureToast(execution)) {
      return { label: execution.exitCode === null ? 'Failed' : `Failed (${execution.exitCode})`, tone: 'failed' };
    }

    if (execution.runOutcome === 'noop') {
      return { label: 'NoOp', tone: 'cancelled' };
    }

    if (execution.runOutcome === 'blocked') {
      return { label: 'Blocked', tone: 'cancelled' };
    }

    if (execution.runOutcome === 'needs-input') {
      return { label: 'Needs input', tone: 'cancelled' };
    }

    // 'stopped' is the new deliberate-kill status from the backend
    // (user pause, Pause-&-Send, watchdog kill). Render as a calm
    // "Stopped" pill, not a failure. Legacy 'cancelled' value stays
    // supported so older in-memory CliExecution records keep rendering.
    if (execution.status === 'stopped' || execution.status === 'cancelled') {
      return { label: 'Stopped', tone: 'cancelled' };
    }

    return null;
  }

  /**
   * Review-pill descriptor: shows the auto-review (Haiku summarizer)
   * status on a card that landed in 4-auto-review. Returns null when
   * there is nothing to show (no run, or the user already moved on).
   */
  readonly reviewBadge = computed<{ label: string; tone: 'generating' | 'ready' | 'failed'; tooltip: string } | null>(() => {
    const s = this.job().summaryState;
    if (!s) return null;
    switch (s.status) {
      case 'generating':
        return { label: 'auto-reviewing', tone: 'generating',
                 tooltip: 'Orchestrator is summarizing the run output (Haiku). The card will become quiet once status.md has been written.' };
      case 'ready':
        return { label: 'review ready', tone: 'ready',
                 tooltip: s.bytesWritten ? `Auto-review wrote ${s.bytesWritten} bytes to status.md.` : 'Auto-review finished.' };
      case 'failed':
        return { label: 'review failed', tone: 'failed',
                 tooltip: s.errorMessage ?? 'Auto-review failed.' };
      default:
        return null;
    }
  });

  readonly autoReviewProcessBadge = computed<{ label: string; tone: 'active' | 'queued' | 'stale' | 'done'; tooltip: string } | null>(() => {
    const job = this.job();
    if (job.state !== '4-auto-review') return null;

    const s = this.autoReviewStatus.status();
    const matchesCurrent = !!s?.currentJob
      && s.currentJob === job.id
      && (!s.currentProject || s.currentProject === job.projectName);

    if (matchesCurrent) {
      return {
        label: 'reviewing now',
        tone: 'active',
        tooltip: 'Auto-review is currently running its multi-aspect pass for this task.'
      };
    }

    if (job.orchestratorVerdict) {
      return {
        label: `review ${job.orchestratorVerdict}`,
        tone: 'done',
        tooltip: `Auto-review has already recorded an orchestrator verdict: ${job.orchestratorVerdict}.`
      };
    }

    if (!s?.lastTickAt) {
      return {
        label: 'review pending',
        tone: 'queued',
        tooltip: 'This task is in Auto Review. The global auto-review status has not loaded yet.'
      };
    }

    const ageMs = Date.now() - Date.parse(s.lastTickAt);
    if (ageMs > 90_000) {
      return {
        label: 'review stale',
        tone: 'stale',
        tooltip: `Auto-review has not completed a tick since ${new Date(s.lastTickAt).toLocaleString()}.`
      };
    }

    return {
      label: 'queued for review',
      tone: 'queued',
      tooltip: `Auto-review is alive. Last tick saw ${s.pending ?? 0} candidate(s); this task is waiting in 4-auto-review.`
    };
  });

  // Lanes that sit in the "Done & Decide" super-column and carry an
  // orchestrator verdict the operator must act on. 4-auto-review is
  // deliberately excluded — it lives in the "active" column and already
  // surfaces its verdict via autoReviewProcessBadge.
  private static readonly HUMAN_DECISION_LANES = new Set([
    '5-human-review', '4-review',
  ]);

  /**
   * Human-decision badge. An escalated / reissue card parked in
   * 5-human-review used to render identically to a Completed card, hiding
   * that a human still has to act ("Failed-Cards sehen aus wie Done").
   * This pill makes the verdict explicit: a loud red "Escalated" / "Needs
   * rework" marker for action-required verdicts, and a calm green "Ready to
   * sign off" for an accepted card awaiting confirmation. Returns null for
   * plain human review (no verdict yet) so undecided cards stay quiet.
   */
  readonly humanReviewBadge = computed<{ label: string; tone: 'attention' | 'accept'; tooltip: string } | null>(() => {
    const job = this.job();
    if (!TaskCardComponent.HUMAN_DECISION_LANES.has(job.state)) return null;
    switch (job.orchestratorVerdict) {
      case 'escalate':
        return {
          label: 'Escalated',
          tone: 'attention',
          tooltip: 'Auto-review escalated this task: the orchestrator could not accept the result and a human must decide what happens next. This is NOT a completed task.'
        };
      case 'reissue':
        return {
          label: 'Needs rework',
          tone: 'attention',
          tooltip: 'Auto-review asked for a reissue: the work needs changes before it can be accepted. Waiting on a human to act.'
        };
      case 'accept':
        return {
          label: 'Ready to sign off',
          tone: 'accept',
          tooltip: 'Auto-review accepted this task. A human just needs to confirm and move it to Completed.'
        };
      default:
        return null;
    }
  });

  /**
   * Host-level "this card needs a human" flag. Drives the red left ribbon +
   * faint tint that visually separates an escalated / reissue card from the
   * Completed/Archive cards it shares the "Done & Decide" column with.
   */
  readonly needsAttention = computed(() => {
    const job = this.job();
    if (!TaskCardComponent.HUMAN_DECISION_LANES.has(job.state)) return false;
    return job.orchestratorVerdict === 'escalate' || job.orchestratorVerdict === 'reissue';
  });

  readonly outcomeIssueBadge = computed<{ label: string; tone: 'info' | 'warn' | 'high'; tooltip: string } | null>(() => {
    const issue = this.job().outcomeIssue;
    if (!issue) return null;
    const severity = (issue.severity ?? '').toLowerCase();
    const tone = severity === 'high' ? 'high' : severity === 'warn' ? 'warn' : 'info';
    const seen = issue.lastSeenAt ? `\nLast seen: ${formatShortTime(issue.lastSeenAt)}` : '';
    const summary = issue.summary ? `\n\n${issue.summary}` : '';
    return {
      label: issue.label || issue.kind,
      tone,
      tooltip: `Runner outcome issue: ${issue.kind}${seen}${summary}`
    };
  });

  /**
   * Card-level "code review running" flag. Reads the shared
   * {@link CodeReviewActivityStore} singleton the detail-pane panel marks
   * while a user-triggered review is in flight, so the operator sees the
   * pass progressing on the board even after navigating away from the task
   * (the user's "Progress an die Karte" requirement). Ephemeral: clears when
   * the synchronous review call resolves.
   */
  readonly codeReviewRunning = computed(() => {
    const job = this.job();
    return this.codeReviewActivity.isRunning(
      CodeReviewActivityStore.key(job.watchPath, job.id),
    );
  });

  /** Hot-state threshold: amber pill once the loop is at 80% of the iteration cap. */
  readonly loopHot = computed(() => {
    const al = this.job().autoLoop;
    if (!al || al.maxIterations <= 0) return false;
    return al.iteration / al.maxIterations >= 0.8;
  });

  loopTooltip(al: AutoLoopSnapshot): string {
    const tokenLine = `${al.tokensUsed.toLocaleString()} / ${al.maxTokens.toLocaleString()} orchestrator tokens`;
    const startedAt = (() => { try { return new Date(al.startedAt).toLocaleString(); } catch { return al.startedAt; } })();
    const lastQ = (al.lastQuestion ?? '').slice(0, 160);
    const lastErr = al.lastError ? `\nLast error: ${al.lastError}` : '';
    return `Auto-loop: orchestrator answering NEEDS_INPUT for this task.\n` +
           `Iteration ${al.iteration} of ${al.maxIterations}.\n` +
           `${tokenLine}.\nStarted ${startedAt}.${lastErr}\n\nLast question: ${lastQ}${(al.lastQuestion ?? '').length > 160 ? '...' : ''}`;
  }

  pendingTooltip(pi: PendingIntent): string {
    const when = (() => {
      try { return new Date(pi.savedAt).toLocaleString(); }
      catch { return pi.savedAt; }
    })();
    const preview = (pi.prompt ?? '').slice(0, 120);
    return `Pending follow-up (${pi.mode}) saved ${when}.\nWill run on next auto-pickup.\n\n${preview}${(pi.prompt ?? '').length > 120 ? '...' : ''}`;
  }

  /** Compact tokens label: 850 -> "850", 2400 -> "2.4k", 850000 -> "850k", 3_100_000 -> "3.1M". */
  formatTokens(n: number): string { return formatTokens(n); }

  /**
   * Token-bubble descriptor: returns null when the task has no recorded
   * orchestrator activity (input + output + cacheRead + cacheWrite == 0).
   * Tier thresholds match the prompt: < 50k neutral, < 500k blue,
   * < 5M mauve, otherwise peach.
   */
  readonly tokenBubble = computed(() => buildTokenBubble(this.job().tokenSummary));

  readonly agentIcon = computed(() => {
    const t = this.job().cliType;
    return t ? cliTypeIcon(t) : '🤖';
  });

  readonly effectiveModelChip = computed(() =>
    buildEffectiveModelChip(this.job(), this.clients.resolve(this.job().ownerClientId))
  );

  readonly identity = computed(() => projectIdentity(this.job().projectName));

  /** Epic container card: drives the "EPIC" badge in the title row. */
  readonly isEpic = computed(() => this.job().kind === 'epic');

  /** Parent epic id when this card is a sub-task, else null (drives the "↳ epic" chip). */
  readonly subTaskEpicId = computed(() => {
    const id = this.job().epicId;
    return id && id.trim().length > 0 ? id : null;
  });

  readonly isRunning = computed(() =>
    this.job().state === '3-progress' && this.job().execution?.status === 'running'
  );

  /**
   * F34: dependsOn targets that are known and not yet complete. Drives the
   * card's `waiting on KEY` badge. Cards with no dependsOn edges short-circuit
   * before reading the board snapshot, so they never depend on `jobs()` and
   * the O(N) state lookup is paid only by the few cards that have dependencies.
   * Targets absent from the current board view are skipped (no false positive),
   * and completed/archived targets are satisfied.
   */
  readonly waitingOn = computed<string[]>(() => {
    const deps = this.job().references?.dependsOn ?? [];
    if (deps.length === 0) return [];
    const stateByKey = new Map<string, string>();
    for (const t of this.jobs.jobs()) {
      const k = (t.key ?? '').trim();
      if (k) stateByKey.set(k.toUpperCase(), t.state);
    }
    return deps.filter((dep) => {
      const st = stateByKey.get(dep.trim().toUpperCase());
      if (st === undefined) return false;
      return st !== '6-completed' && st !== '7-archive';
    });
  });

  /** Compact badge label: first waiting key, with a "+N" suffix for the rest. */
  readonly waitingOnLabel = computed<string | null>(() => {
    const waiting = this.waitingOn();
    if (waiting.length === 0) return null;
    return waiting.length === 1 ? waiting[0] : `${waiting[0]} +${waiting.length - 1}`;
  });

  readonly waitingOnTooltip = computed(() => {
    const waiting = this.waitingOn();
    if (waiting.length === 0) return '';
    return `Waiting on ${waiting.join(', ')} to complete before this task is workable.`;
  });

  readonly relativeActivity = computed(() => {
    const dateStr = this.job().lastActivity;
    if (!dateStr) return 'never';
    const diff = nowTick() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return mins + 'm ago';
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return hrs + 'h ago';
    return Math.floor(hrs / 24) + 'd ago';
  });

  // Context menu: copy actions + epic assignment (way 2).
  private readonly notifications = inject(NotificationService);
  private readonly jobs = inject(TaskService);
  private readonly boardFilters = inject(BoardFiltersService);
  readonly cardContextMenu = signal<{ x: number; y: number } | null>(null);
  /** Epics in this card's project, loaded on right-click for the assign submenu. */
  private readonly epicsForMenu = signal<EpicRollup[]>([]);

  private static readonly EPIC_ASSIGN_PREFIX = 'epic-assign:';
  private static readonly EPIC_DETACH_ID = 'epic-detach';
  private static readonly FILTER_DEPENDENTS_ID = 'filter-dependents';

  readonly cardCtxMenuItems = computed<readonly MenuItem[]>(() => {
    const job = this.job();
    const items: MenuItem[] = [
      { kind: 'row', id: 'copy-name', label: 'Copy Name' },
      { kind: 'row', id: 'copy-id', label: 'Copy ID' },
    ];
    if (job.key) {
      items.push({ kind: 'row', id: 'copy-key', label: `Copy Key (${job.key})` });
      items.push({
        kind: 'row',
        id: TaskCardComponent.FILTER_DEPENDENTS_ID,
        label: `Filter: tasks depending on ${job.key}`,
      });
    }

    // Epic assignment is only meaningful for ordinary task cards - an epic is
    // not a sub-task of another epic.
    if (!this.isEpic()) {
      const epics = this.epicsForMenu();
      const currentEpicId = this.subTaskEpicId();
      items.push({ kind: 'separator' });
      items.push({ kind: 'header', label: 'Epic' });
      if (epics.length === 0 && !currentEpicId) {
        items.push({ kind: 'row', id: 'epic-none', label: 'No epics in this project', disabled: true });
      } else {
        for (const epic of epics) {
          items.push({
            kind: 'row',
            id: TaskCardComponent.EPIC_ASSIGN_PREFIX + epic.id,
            label: epic.title || epic.id,
            active: epic.id === currentEpicId,
          });
        }
        if (currentEpicId) {
          items.push({ kind: 'row', id: TaskCardComponent.EPIC_DETACH_ID, label: 'Detach from epic' });
        }
      }
    }
    return items;
  });

  openCardContextMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.cardContextMenu.set({ x: event.clientX, y: event.clientY });
    // Refresh the assign list each open (only for task cards). Best-effort:
    // the section just shows "No epics" on failure.
    if (!this.isEpic()) {
      const watchPath = this.job().watchPath;
      this.jobs.getEpics().subscribe({
        next: (list) => this.epicsForMenu.set((list ?? []).filter((e) => e.watchPath === watchPath)),
        error: () => this.epicsForMenu.set([]),
      });
    }
  }

  closeCardContextMenu(): void {
    this.cardContextMenu.set(null);
  }

  onCardCtxMenuItemClick(ev: MenuItemClickEvent): void {
    const job = this.job();

    if (ev.id.startsWith(TaskCardComponent.EPIC_ASSIGN_PREFIX)) {
      const epicId = ev.id.slice(TaskCardComponent.EPIC_ASSIGN_PREFIX.length);
      if (epicId === this.subTaskEpicId()) return; // already in this epic
      this.assignEpic(epicId);
      return;
    }
    if (ev.id === TaskCardComponent.EPIC_DETACH_ID) {
      this.assignEpic(null);
      return;
    }
    if (ev.id === TaskCardComponent.FILTER_DEPENDENTS_ID && job.key) {
      this.boardFilters.setDependsOnFilter(job.key);
      this.notifications.info(`Filtering to tasks that depend on ${job.key}`);
      return;
    }

    let text = '';
    let label = '';
    if (ev.id === 'copy-name') { text = job.title || job.id; label = 'Name'; }
    else if (ev.id === 'copy-id') { text = job.id; label = 'ID'; }
    else if (ev.id === 'copy-key' && job.key) { text = job.key; label = 'Key'; }
    if (text) {
      copyTextToClipboard(text).then(ok => {
        if (ok) this.notifications.success(`${label} copied`);
      });
    }
  }

  /** Way 2: attach (epicId) or detach (null) this task, then refresh the board. */
  private assignEpic(epicId: string | null): void {
    const job = this.job();
    this.jobs.setJobEpic(job.id, epicId, job.watchPath).subscribe({
      next: () => {
        const epic = this.epicsForMenu().find((e) => e.id === epicId);
        this.notifications.success(
          epicId ? `Assigned to epic: ${epic?.title ?? epicId}` : 'Detached from epic',
        );
        this.jobs.refresh(true);
      },
      error: () => this.notifications.error('Could not update epic assignment'),
    });
  }
}
