import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { DomSanitizer, type SafeResourceUrl } from '@angular/platform-browser';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskService } from '../../../../services/task.service';
import {
  ProjectUrlProbeService,
  type ProjectUrlReadiness,
} from '../../../../services/project-url-probe.service';
import type { RegistryProjectUrl } from '../../../../models/task.model';
import { ProjectUrlLookupService } from '../../services/project-url-lookup.service';

/** Address-bar status pill vocabulary for the embedded preview. */
type PreviewPill = 'running' | 'offline' | 'checking' | 'building' | 'blocked' | 'failed';

interface StartFailure {
  explanation: string;
  command: string;
  cwd: string;
}

/**
 * AGT-2067 — embedded Project URL preview tab.
 *
 * Renders a configured Project URL inside a **sandboxed `<iframe>`** as its own
 * editor tab (one tab per URL), replacing the old `window.open` browser jump.
 * Opening the Orchestrator side sheet beside it *is* the split view — this
 * component owns only the left pane: a read-only address bar (URL + live status
 * pill, reload, open-externally, settings deep link) plus the iframe and its
 * load / offline / blocked state machine.
 *
 * State machine (concept §1.3), driven by {@link ProjectUrlProbeService} plus
 * the iframe `load` event and a load-timeout heuristic:
 * - `resolving` → looking the URL record up in the registry;
 * - `not-found` → the URL was removed from the project (no stuck spinner);
 * - resolved + `offline` → "Not running" card with a **Start** button when the
 *   URL has a start rule, else a note that it has no start command;
 * - resolved + `running`/`unknown` → mount the iframe; a spinner overlays it
 *   until `load` fires, and if `load` never fires within {@link LOAD_TIMEOUT_MS}
 *   while the server is reachable we surface a "may refuse to be embedded"
 *   banner (X-Frame-Options / CSP) with an always-available browser escape hatch.
 */
@Component({
  selector: 'app-project-url-preview-tab',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './project-url-preview-tab.component.html',
  styleUrl: './project-url-preview-tab.component.scss',
})
export class ProjectUrlPreviewTabComponent {
  readonly projectName = input.required<string>();
  readonly urlId = input.required<string>();

  /** Deep-link request to the Project Hub "Project URLs" management page. */
  readonly openSettings = output<{ projectName: string }>();

  private readonly lookup = inject(ProjectUrlLookupService);
  private readonly probe = inject(ProjectUrlProbeService);
  private readonly taskService = inject(TaskService);
  private readonly sanitizer = inject(DomSanitizer);

  /** ~6s: a framed dev server that has not fired `load` by now, while the probe
   *  says it is reachable, is treated as "probably refuses embedding". */
  private readonly LOAD_TIMEOUT_MS = 6000;
  private readonly START_READY_TIMEOUT_MS = 20_000;
  private readonly START_POLL_MS = 750;

  readonly resolveState = signal<'resolving' | 'resolved' | 'not-found' | 'error'>('resolving');
  readonly projectId = signal<string | null>(null);
  readonly urlRecord = signal<RegistryProjectUrl | null>(null);
  readonly frameState = signal<'loading' | 'loaded' | 'blocked'>('loading');
  readonly building = signal(false);
  readonly repositoryPath = signal<string | null>(null);
  readonly rootPath = signal<string | null>(null);
  readonly startFailure = signal<StartFailure | null>(null);
  /** Bumping this re-navigates the iframe (forces a fresh `[src]` reference). */
  private readonly reloadNonce = signal(0);

  private loadTimer: ReturnType<typeof setTimeout> | null = null;
  private startTimer: ReturnType<typeof setTimeout> | null = null;
  private startDeadline = 0;

  readonly readiness = computed<ProjectUrlReadiness>(() => {
    const projectId = this.projectId();
    const url = this.urlRecord();
    return projectId && url
      ? this.probe.readinessFor(projectId, url.id)
      : { kind: 'unknown', statusCode: null, framePolicy: 'unknown', detail: null, durationMs: null };
  });

  /** Live probe status for the resolved URL (`unknown` before it resolves). */
  readonly probeStatus = computed(() => {
    const projectId = this.projectId();
    const u = this.urlRecord();
    return projectId && u ? this.probe.statusFor(projectId, u.id) : 'unknown';
  });

  /** Embed the iframe when the URL resolved and the server is not known-offline
   *  (running or still-unknown → optimistic load). Offline shows the card. */
  readonly shouldEmbed = computed(() =>
    this.resolveState() === 'resolved'
      && !this.building()
      && !this.startFailure()
      && this.readiness().kind === 'healthy',
  );

  readonly readinessPending = computed(() =>
    this.resolveState() === 'resolved'
      && !this.building()
      && !this.startFailure()
      && this.readiness().kind === 'unknown',
  );

  /** Sandboxed iframe source; recomputes on reload but not on probe re-ticks so
   *  a steady "running" status never remounts a loaded frame. */
  readonly iframeSrc = computed<SafeResourceUrl | null>(() => {
    const u = this.urlRecord();
    if (!u) return null;
    void this.reloadNonce();
    return this.sanitizer.bypassSecurityTrustResourceUrl(u.url);
  });

  readonly statusPill = computed<PreviewPill>(() => {
    if (this.building()) return 'building';
    if (this.startFailure()) return 'failed';
    if (this.resolveState() !== 'resolved') return 'checking';
    if (this.frameState() === 'blocked') return 'blocked';
    const s = this.probeStatus();
    return s === 'unknown' ? 'checking' : s;
  });

  readonly canReload = computed(() => this.resolveState() === 'resolved' && !this.building());

  constructor() {
    // Re-resolve whenever the bound project / url changes (established panel
    // pattern: an effect that fires an HTTP refresh on input change).
    effect(() => {
      const name = this.projectName();
      const id = this.urlId();
      this.resolveRecord(name, id);
    });

    // Arm (or clear) the load-timeout heuristic whenever the iframe (re)mounts.
    effect(() => {
      const embed = this.shouldEmbed();
      this.reloadNonce(); // re-arm on reload
      this.clearLoadTimer();
      if (!embed) return;
      this.frameState.set('loading');
      this.loadTimer = setTimeout(() => this.onLoadTimeout(), this.LOAD_TIMEOUT_MS);
    });

    inject(DestroyRef).onDestroy(() => {
      this.clearLoadTimer();
      this.clearStartTimer();
    });
  }

  private resolveRecord(name: string, id: string): void {
    this.clearStartTimer();
    this.building.set(false);
    this.startFailure.set(null);
    this.resolveState.set('resolving');
    this.lookup.resolve(name, id).subscribe({
      next: res => {
        // Guard against a stale response after the inputs changed again.
        if (name !== this.projectName() || id !== this.urlId()) return;
        if (!res) {
          this.projectId.set(null);
          this.urlRecord.set(null);
          this.repositoryPath.set(null);
          this.rootPath.set(null);
          this.resolveState.set('not-found');
          return;
        }
        this.projectId.set(res.projectId);
        this.urlRecord.set(res.url);
        this.repositoryPath.set(res.repositoryPath);
        this.rootPath.set(res.rootPath);
        this.frameState.set('loading');
        this.resolveState.set('resolved');
      },
      error: () => {
        if (name !== this.projectName() || id !== this.urlId()) return;
        this.resolveState.set('error');
      },
    });
  }

  /** iframe finished (loaded a page — we cannot read cross-origin, only that it
   *  navigated). Clears the suspected-block timer. */
  onFrameLoad(): void {
    if (this.probeStatus() !== 'running') return;
    this.frameState.set('loaded');
    this.clearLoadTimer();
  }

  private onLoadTimeout(): void {
    if (this.frameState() !== 'loading') return;
    // Only escalate to "blocked" when the server is actually reachable; an
    // unreachable server is an offline case, not an embed refusal.
    if (this.probeStatus() === 'running') {
      const url = this.urlRecord()?.url;
      console.warn('[url-preview] suspected embed refusal (X-Frame-Options / CSP)', { url });
      this.frameState.set('blocked');
    }
  }

  reload(): void {
    if (!this.canReload()) return;
    const projectId = this.projectId();
    const url = this.urlRecord();
    if (!projectId || !url) return;
    const wasRunning = this.probeStatus() === 'running';
    this.probe.refresh(projectId, url.id);
    if (wasRunning) {
      this.frameState.set('loading');
      this.reloadNonce.update(n => n + 1);
    }
  }

  /** Build & start (or restart) the URL's dev server, then re-probe — the same
   *  call the Project Hub "Restart" button uses. */
  start(): void {
    const projId = this.projectId();
    const u = this.urlRecord();
    if (!projId || !u || !u.startRule || this.building()) return;
    const rule = u.startRule;
    this.clearStartTimer();
    this.startFailure.set(null);
    this.building.set(true);
    this.taskService.startProjectUrl(projId, u.id).subscribe({
      next: () => this.afterStart(u.id),
      error: (error: HttpErrorResponse) => {
        const body: Record<string, unknown> = error.error && typeof error.error === 'object'
          ? error.error as Record<string, unknown>
          : {};
        this.startFailure.set({
          explanation: typeof body['error'] === 'string'
            ? body['error']
            : 'The dev server could not be started.',
          command: typeof body['command'] === 'string' ? body['command'] : rule.command,
          cwd: typeof body['cwd'] === 'string' && body['cwd'].trim()
            ? body['cwd']
            : this.effectiveCwd(),
        });
        this.building.set(false);
      },
    });
  }

  effectiveCwd(): string {
    return this.urlRecord()?.startRule?.cwd
      || this.repositoryPath()
      || this.rootPath()
      || 'Not configured';
  }

  private afterStart(urlId: string): void {
    const projectId = this.projectId();
    if (!projectId) return;
    this.startDeadline = Date.now() + this.START_READY_TIMEOUT_MS;
    this.probe.refresh(projectId, urlId);
    this.startTimer = setTimeout(() => this.checkStartReadiness(projectId, urlId), this.START_POLL_MS);
  }

  private checkStartReadiness(projectId: string, urlId: string): void {
    this.startTimer = null;
    if (!this.building() || this.projectId() !== projectId || this.urlRecord()?.id !== urlId) return;

    if (this.probe.signalFor(projectId, urlId)().kind === 'healthy') {
      this.building.set(false);
      this.frameState.set('loading');
      this.reloadNonce.update(n => n + 1);
      return;
    }

    if (Date.now() >= this.startDeadline) {
      const rule = this.urlRecord()?.startRule;
      this.startFailure.set({
        explanation: 'The command started, but the URL did not become reachable within 20 seconds.',
        command: rule?.command ?? 'Not configured',
        cwd: this.effectiveCwd(),
      });
      this.building.set(false);
      return;
    }

    this.probe.refresh(projectId, urlId);
    this.startTimer = setTimeout(() => this.checkStartReadiness(projectId, urlId), this.START_POLL_MS);
  }

  openExternal(): void {
    const u = this.urlRecord();
    if (u) window.open(u.url, '_blank', 'noopener');
  }

  onSettings(): void {
    this.openSettings.emit({ projectName: this.projectName() });
  }

  private clearLoadTimer(): void {
    if (this.loadTimer) {
      clearTimeout(this.loadTimer);
      this.loadTimer = null;
    }
  }

  private clearStartTimer(): void {
    if (this.startTimer) {
      clearTimeout(this.startTimer);
      this.startTimer = null;
    }
  }
}
