import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { DomSanitizer, type SafeResourceUrl } from '@angular/platform-browser';
import { finalize } from 'rxjs';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '../../../../components/menu';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskService } from '../../../../services/task.service';
import {
  ProjectUrlProbeService,
  type ProjectUrlReadiness,
} from '../../../../services/project-url-probe.service';
import type { RegistryProjectUrl } from '../../../../models/task.model';
import { ProjectUrlLookupService } from '../../services/project-url-lookup.service';
import { ProjectUrlProcessController } from '../../services/project-url-process.controller';
import { ProjectUrlProcessConsoleComponent } from '../project-url-process-console/project-url-process-console';
import {
  ProjectUrlSettingsDialogComponent,
  type ProjectUrlSettingsValue,
} from '../project-url-settings-dialog/project-url-settings-dialog';
import {
  inspectPreviewFrame,
  type PreviewFrameState,
} from './project-url-frame-readiness';

/** Address-bar status pill vocabulary for the embedded preview. */
type PreviewPill =
  | 'running' | 'rendered' | 'offline' | 'navigating' | 'checking' | 'building'
  | 'blocked' | 'blank' | 'error' | 'unconfirmed' | 'failed';

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
 * iframe navigation, DOM inspection where origin policy permits it, and a
 * bounded load fallback:
 * - `resolving` → looking the URL record up in the registry;
 * - `not-found` → the URL was removed from the project (no stuck spinner);
 * - resolved + `offline` → "Not running" card with a **Start** button when the
 *   URL has a start rule, else a note that it has no start command;
 * - resolved + `running`/`unknown` → mount the iframe; a spinner overlays it
 *   while it navigates. A load event triggers readiness inspection rather than
 *   implying success. If navigation cannot settle within {@link LOAD_TIMEOUT_MS},
 *   an actionable fallback replaces the unexplained loading canvas.
 */
@Component({
  selector: 'app-project-url-preview-tab',
  standalone: true,
  imports: [
    StudioIconComponent,
    TooltipDirective,
    MenuComponent,
    ProjectUrlProcessConsoleComponent,
    ProjectUrlSettingsDialogComponent,
  ],
  providers: [ProjectUrlProcessController],
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
  readonly process = inject(ProjectUrlProcessController);

  /** ~6s: a framed dev server that has not fired `load` by now, while the probe
   *  says it is reachable, is treated as "probably refuses embedding". */
  private readonly LOAD_TIMEOUT_MS = 6000;
  private readonly START_READY_TIMEOUT_MS = 20_000;
  private readonly START_POLL_MS = 750;

  readonly resolveState = signal<'resolving' | 'resolved' | 'not-found' | 'error'>('resolving');
  readonly projectId = signal<string | null>(null);
  readonly urlRecord = signal<RegistryProjectUrl | null>(null);
  readonly frameState = signal<PreviewFrameState>('navigating');
  readonly frameDetail = signal('Waiting for the embedded browser to navigate.');
  readonly building = signal(false);
  readonly repositoryPath = signal<string | null>(null);
  readonly rootPath = signal<string | null>(null);
  readonly startFailure = signal<StartFailure | null>(null);
  readonly menuOpen = signal(false);
  readonly menuPosition = signal<{ x: number; y: number } | null>(null);
  readonly settingsOpen = signal(false);
  readonly settingsSaving = signal(false);
  readonly settingsError = signal<string | null>(null);
  /** Bumping this re-navigates the iframe (forces a fresh `[src]` reference). */
  private readonly reloadNonce = signal(0);
  readonly previewFrame = viewChild<ElementRef<HTMLIFrameElement>>('previewFrame');

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
    // Embedded: the pill reports frame truth (navigating / rendered / blank /
    // blocked / error / unconfirmed), never bare server reachability.
    if (this.shouldEmbed()) return this.frameState();
    const s = this.probeStatus();
    return s === 'unknown' ? 'checking' : s;
  });

  readonly stateBadgeStatus = computed<PreviewPill>(() => {
    if (this.startFailure()) return 'failed';
    if (this.building()) return 'building';
    if (this.readiness().kind === 'frame-blocked') return 'blocked';
    if (this.readiness().kind === 'http-error') return 'failed';
    return 'offline';
  });

  readonly canReload = computed(() => this.resolveState() === 'resolved' && !this.building());

  readonly menuItems = computed<readonly MenuItem[]>(() => {
    const session = this.process.session();
    const ownsRunning = session?.state === 'running' || session?.state === 'starting';
    const hasStartRule = Boolean(this.urlRecord()?.startRule);
    return [
      { kind: 'header', label: this.urlRecord()?.label ?? 'Embed' },
      {
        kind: 'row',
        id: 'start',
        label: this.probeStatus() === 'running' || ownsRunning ? 'Restart' : 'Start',
        hint: this.urlRecord()?.startRule?.command,
        disabled: !hasStartRule || this.building() || this.process.stopping(),
      },
      { kind: 'row', id: 'console', label: 'Show live console', disabled: !session },
      { kind: 'row', id: 'stop', label: 'Stop server', danger: true, disabled: !ownsRunning || this.process.stopping() },
      { kind: 'separator' },
      { kind: 'row', id: 'settings', label: 'Embed settings', disabled: !this.urlRecord() },
      { kind: 'row', id: 'external', label: 'Open externally', disabled: !this.urlRecord() },
    ];
  });

  readonly navigationDetail = computed(() => this.probeStatus() === 'running'
    ? 'Server reachable. Waiting for the embedded browser to finish navigating…'
    : 'Checking server reachability and embedded navigation…');

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
      this.beginNavigation();
      if (!embed) return;
      this.loadTimer = setTimeout(() => this.onLoadTimeout(), this.LOAD_TIMEOUT_MS);
    });

    inject(DestroyRef).onDestroy(() => {
      this.clearLoadTimer();
      this.clearStartTimer();
    });
  }

  private resolveRecord(name: string, id: string): void {
    this.clearStartTimer();
    this.process.reset();
    this.building.set(false);
    this.startFailure.set(null);
    this.menuOpen.set(false);
    this.settingsOpen.set(false);
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
        this.beginNavigation();
        this.resolveState.set('resolved');
        this.process.refresh(res.projectId, res.url.id);
      },
      error: () => {
        if (name !== this.projectName() || id !== this.urlId()) return;
        this.resolveState.set('error');
      },
    });
  }

  /** A load event starts readiness inspection; it is not success by itself. */
  onFrameLoad(frame: HTMLIFrameElement): void {
    this.clearLoadTimer();
    const inspection = inspectPreviewFrame(frame, this.urlRecord()?.url ?? 'about:blank');
    this.frameState.set(inspection.state);
    this.frameDetail.set(inspection.detail);
  }

  onFrameError(): void {
    this.clearLoadTimer();
    this.frameState.set('error');
    this.frameDetail.set('The embedded browser could not navigate to the configured URL.');
  }

  private onLoadTimeout(): void {
    if (this.frameState() !== 'navigating') return;
    if (this.probeStatus() === 'running') {
      this.frameState.set('error');
      this.frameDetail.set('The server is reachable, but the frame did not finish navigating within 6 seconds.');
      return;
    }
    this.frameState.set('error');
    this.frameDetail.set('The frame did not finish navigating within 6 seconds, and server reachability could not be confirmed.');
  }

  reload(): void {
    if (!this.canReload()) return;
    const projectId = this.projectId();
    const url = this.urlRecord();
    if (!projectId || !url) return;
    const frame = this.previewFrame()?.nativeElement;
    if (this.shouldEmbed() && frame) {
      // A mounted frame reloads through a real navigation: assigning the same
      // URL to the native src property preserves the configured URL exactly.
      // A dead server surfaces through inspection or the probe's TTL re-check.
      this.beginNavigation();
      this.clearLoadTimer();
      this.loadTimer = setTimeout(() => this.onLoadTimeout(), this.LOAD_TIMEOUT_MS);
      frame.src = url.url;
      return;
    }
    // Card states re-check readiness so a recovered server can mount again.
    this.probe.refresh(projectId, url.id);
  }

  /** Start or restart the owned process, expose its output in place, and keep
   * the existing bounded readiness/retry loop before mounting the iframe. */
  start(): void {
    const projId = this.projectId();
    const u = this.urlRecord();
    if (!projId || !u || !u.startRule || this.building() || this.process.stopping()) return;
    const rule = u.startRule;
    this.clearStartTimer();
    this.startFailure.set(null);
    this.building.set(true);
    this.process.start(projId, u, this.effectiveCwd()).subscribe({
      next: () => this.afterStart(u.id),
      error: (error: HttpErrorResponse) => {
        const body: Record<string, unknown> = error.error && typeof error.error === 'object'
          ? error.error as Record<string, unknown>
          : {};
        const failure: StartFailure = {
          explanation: typeof body['error'] === 'string'
            ? body['error']
            : 'The dev server could not be started.',
          command: typeof body['command'] === 'string' ? body['command'] : rule.command,
          cwd: typeof body['cwd'] === 'string' && body['cwd'].trim()
            ? body['cwd']
            : this.effectiveCwd(),
        };
        this.startFailure.set(failure);
        this.process.failStart(failure.explanation, failure.command, failure.cwd);
        this.building.set(false);
      },
    });
  }

  stop(): void {
    const projectId = this.projectId();
    const url = this.urlRecord();
    if (!projectId || !url || this.process.stopping()) return;
    this.clearStartTimer();
    this.building.set(false);
    this.startFailure.set(null);
    this.process.stop(projectId, url.id).subscribe({
      next: () => this.probe.refresh(projectId, url.id),
      error: error => this.process.appendError(this.errorMessage(error)),
    });
  }

  showMenu(event?: MouseEvent): void {
    event?.preventDefault();
    this.menuPosition.set(event ? { x: event.clientX, y: event.clientY } : null);
    this.menuOpen.set(true);
    const projectId = this.projectId();
    const urlId = this.urlRecord()?.id;
    if (projectId && urlId) this.process.refresh(projectId, urlId);
  }

  onMenuItem(event: MenuItemClickEvent): void {
    switch (event.id) {
      case 'start': this.start(); break;
      case 'console': this.process.consoleOpen.set(true); break;
      case 'stop': this.process.consoleOpen.set(true); this.stop(); break;
      case 'settings': this.onSettings(); break;
      case 'external': this.openExternal(); break;
    }
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
      this.beginNavigation();
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
    if (!this.urlRecord()) {
      this.openSettings.emit({ projectName: this.projectName() });
      return;
    }
    this.settingsError.set(null);
    this.settingsOpen.set(true);
  }

  saveSettings(value: ProjectUrlSettingsValue): void {
    const projectId = this.projectId();
    const current = this.urlRecord();
    if (!projectId || !current || this.settingsSaving()) return;
    this.settingsSaving.set(true);
    this.settingsError.set(null);
    this.taskService.updateProjectUrl(projectId, current.id, value).pipe(
      finalize(() => this.settingsSaving.set(false)),
    ).subscribe({
      next: () => {
        this.urlRecord.set({ ...current, ...value });
        this.settingsOpen.set(false);
        this.beginNavigation();
        this.reloadNonce.update(n => n + 1);
        this.probe.refresh(projectId, current.id);
      },
      error: error => this.settingsError.set(this.errorMessage(error)),
    });
  }

  private errorMessage(error: unknown): string {
    const value = error as { error?: string | { error?: string; message?: string }; message?: string };
    if (typeof value?.error === 'string') return value.error;
    return value?.error?.error ?? value?.error?.message ?? value?.message ?? 'The operation failed.';
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

  private beginNavigation(): void {
    this.frameState.set('navigating');
    this.frameDetail.set('Waiting for the embedded browser to navigate.');
  }
}
