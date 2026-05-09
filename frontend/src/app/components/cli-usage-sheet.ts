import { Component, OnDestroy, OnInit, signal, computed } from '@angular/core';
import { JobService } from '../services/job.service';
import { CliConsoleComponent } from './cli-console';
import { QuotaStripComponent } from './quota-strip';
import {
  CliOutputLine,
  CliSessionInfo,
  CliType,
  CliUsageProjectGroup,
  CliUsageReport,
  CliUsageSection
} from '../models/job.model';

interface SelectedSession {
  cliType: CliType;
  projectName: string;
  session: CliSessionInfo;
}

/**
 * Right-hand sidesheet that combines two collapsible segments:
 *   - Quota: subscription / rate-limit visualisation (rendered by QuotaStripComponent).
 *   - Sessions: per-CLI per-project session list with token usage and (best-effort) output.
 *
 * Unlike a typical floating sidesheet this component participates in normal
 * document flow — the parent layout in `app.ts` keeps it next to the main
 * content via flexbox. When closed the host width collapses to 0 so the rest
 * of the UI gets the space back without an overlay covering anything.
 *
 * Live session output streaming for Claude/Codex isn't wired yet; we surface
 * what's already on disk plus session metadata.
 */
@Component({
  selector: 'app-cli-usage-sheet',
  standalone: true,
  imports: [CliConsoleComponent, QuotaStripComponent],
  template: `
    <aside class="sheet" [class.sheet--open]="open()">
      <header class="sheet__header">
        <div>
          <h2 class="sheet__title">CLI Usage</h2>
          <div class="sheet__subtitle">Quota and live sessions per CLI</div>
        </div>
        <div class="sheet__header-actions">
          <button class="sheet__btn" (click)="refreshSessions()" [disabled]="loading()" title="Reload session list">
            {{ loading() ? '⏳' : '↻' }}
          </button>
          <button class="sheet__close" type="button" (click)="closed.emit()" title="Close panel">✕</button>
        </div>
      </header>

      @if (errorMsg(); as err) {
        <div class="sheet__error">{{ err }}</div>
      }

      <div class="sheet__body">
        <!-- Segment 1: Quota -->
        <section class="seg">
          <button class="seg__head" type="button" (click)="toggleSegment('quota')">
            <span class="seg__chev">{{ isSegmentCollapsed('quota') ? '▶' : '▼' }}</span>
            <span class="seg__title">Quota</span>
            <span class="seg__hint">subscription limits</span>
          </button>
          @if (!isSegmentCollapsed('quota')) {
            <app-quota-strip />
          }
        </section>

        <!-- Segment 2: Sessions -->
        <section class="seg">
          <button class="seg__head" type="button" (click)="toggleSegment('sessions')">
            <span class="seg__chev">{{ isSegmentCollapsed('sessions') ? '▶' : '▼' }}</span>
            <span class="seg__title">Sessions</span>
            <span class="seg__hint">{{ totalSessionCount() }} total</span>
          </button>

          @if (!isSegmentCollapsed('sessions')) {
            <div class="seg__body">
              @for (section of visibleSections(); track section.cliType) {
                <section class="cli-section">
                  <button class="cli-section__head"
                          type="button"
                          (click)="toggleCli(section.cliType)">
                    <span class="cli-section__chev">{{ isCliCollapsed(section.cliType) ? '▶' : '▼' }}</span>
                    <span class="cli-section__title">{{ cliLabel(section.cliType) }}</span>
                    @if (section.available && section.version) {
                      <span class="pill pill--ok">{{ section.version }}</span>
                    }
                    <span class="cli-section__count">
                      {{ totalSessions(section) }} sessions
                    </span>
                  </button>

                  @if (!isCliCollapsed(section.cliType)) {
                    @if (section.error) {
                      <div class="sheet__error sheet__error--inline">{{ section.error }}</div>
                    }
                    @if (section.projects.length === 0) {
                      <div class="sheet__empty">No sessions found yet for {{ cliLabel(section.cliType) }}.</div>
                    }
                    @for (project of section.projects; track project.projectName) {
                      <div class="proj">
                        <button class="proj__head" type="button" [title]="project.rootPath || ''" (click)="toggleProject(section.cliType, project)">
                          <span class="proj__chev" [class.proj__chev--open]="!isProjectCollapsed(section.cliType, project)"></span>
                          <span class="proj__name">📁 {{ project.projectName }}</span>
                          <span class="proj__meta">{{ project.sessions.length }} sessions</span>
                        </button>
                        <div class="proj__summary">
                          @if (latestSession(project)?.updatedAt; as latest) {
                            <span>latest {{ formatTime(latest) }}</span>
                          }
                          @if (usageSessionCount(project) > 0) {
                            <span>{{ usageSessionCount(project) }} with usage</span>
                          } @else {
                            <span>metadata only</span>
                          }
                        </div>
                        @if (!isProjectCollapsed(section.cliType, project)) {
                        <ul class="sess-list">
                          @for (s of project.sessions; track s.id) {
                            <li class="sess"
                                [class.sess--active]="isSelected(section.cliType, project, s)"
                                (click)="select(section.cliType, project, s)">
                              <div class="sess__row">
                                <span class="sess__id" [title]="s.id">
                                  {{ s.label || shortId(s.id) }}
                                </span>
                                @if (s.updatedAt) {
                                  <span class="sess__time">{{ formatTime(s.updatedAt) }}</span>
                                }
                              </div>
                              @if (s.lastUsage?.tokens) {
                                <div class="sess__usage">🪙 {{ s.lastUsage?.tokens }}</div>
                              } @else {
                                <div class="sess__usage sess__usage--muted">No parsed token summary</div>
                              }
                            </li>
                          }
                        </ul>
                        }
                      </div>
                    }
                  }
                </section>
              }
            </div>
          }
        </section>
      </div>

      @if (selected(); as sel) {
        <div class="sheet__detail">
          <div class="sheet__detail-head">
            <strong>{{ cliLabel(sel.cliType) }}</strong>
            · {{ sel.session.label || shortId(sel.session.id) }}
          </div>
          @if (sel.session.lastUsage; as u) {
            <div class="sheet__detail-usage">
              <div><span>Tokens</span><span>{{ u.tokens || '—' }}</span></div>
              <div><span>Changes</span><span>{{ u.changes || '—' }}</span></div>
              <div><span>Requests</span><span>{{ u.requests || '—' }}</span></div>
            </div>
          } @else {
            <div class="sheet__detail-empty">
              This session only has id, project path and last-modified time. No token summary has been parsed yet.
            </div>
          }
          <app-cli-console [lines]="detailLines()" [title]="'Session ' + shortId(sel.session.id)" [bodyMaxHeight]="'30vh'" />
        </div>
      }
    </aside>
  `,
  styles: [`
    /* The host participates in flex layout from app.ts. When closed we collapse
       to zero width so the main content reclaims the space. */
    :host {
      display: block;
      width: 0;
      transition: width 0.22s ease;
      overflow: hidden;
      flex: 0 0 auto;
    }
    :host(.is-open) { width: min(440px, 92vw); }

    .sheet {
      width: min(440px, 92vw);
      height: 100%;
      background: #11111b;
      border-left: 1px solid rgba(255,255,255,0.08);
      display: flex;
      flex-direction: column;
      color: #e2e8f0;
    }
    .sheet__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      padding: 16px 18px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .sheet__subtitle { font-size: 12px; color: #64748b; margin-top: 2px; }
    .sheet__title { margin: 0; font-size: 18px; }
    .sheet__header-actions { display: flex; gap: 8px; align-items: center; }
    .sheet__btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #cbd5e1;
      padding: 4px 10px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 12px;
    }
    .sheet__btn:hover:not(:disabled) { background: rgba(255,255,255,0.1); }
    .sheet__close {
      background: rgba(255,255,255,0.06);
      border: 0;
      color: #cbd5e1;
      width: 28px; height: 28px;
      border-radius: 999px;
      cursor: pointer;
    }
    .sheet__error {
      margin: 12px 18px 0;
      padding: 8px 12px;
      background: rgba(244,63,94,0.1);
      border: 1px solid rgba(244,63,94,0.18);
      color: #fda4af;
      border-radius: 8px;
      font-size: 12px;
    }
    .sheet__error--inline { margin: 0 0 8px; }
    .sheet__body {
      flex: 1;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
    }

    /* Top-level collapsible segment (Quota / Sessions). */
    .seg + .seg { border-top: 1px solid rgba(255,255,255,0.06); }
    .seg__head {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      background: rgba(255,255,255,0.02);
      color: inherit;
      border: 0;
      cursor: pointer;
      font-size: 13px;
      padding: 10px 16px;
      text-align: left;
    }
    .seg__head:hover { background: rgba(255,255,255,0.04); }
    .seg__chev { font-size: 10px; color: #64748b; width: 10px; }
    .seg__title { font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; font-size: 11px; }
    .seg__hint { margin-left: auto; font-size: 11px; color: #64748b; font-weight: 400; text-transform: none; letter-spacing: 0; }
    .seg__body { padding: 12px 14px; display: flex; flex-direction: column; gap: 12px; }

    /* Per-CLI section inside the Sessions segment. */
    .cli-section {
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 12px;
      background: rgba(255,255,255,0.02);
      padding: 10px;
    }
    .cli-section__head {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      background: transparent;
      color: inherit;
      border: 0;
      cursor: pointer;
      font-size: 13px;
      padding: 4px 2px;
    }
    .cli-section__chev { font-size: 10px; color: #64748b; width: 10px; }
    .cli-section__title { font-weight: 600; }
    .cli-section__count { margin-left: auto; font-size: 11px; color: #64748b; }
    .pill {
      font-size: 10px;
      padding: 2px 6px;
      border-radius: 999px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .pill--ok { background: rgba(34,197,94,0.12); color: #4ade80; }
    .sheet__empty { padding: 8px 4px; color: #64748b; font-size: 12px; }
    .proj {
      margin-top: 8px;
      padding-top: 8px;
      border-top: 1px dashed rgba(255,255,255,0.06);
    }
    .proj__head {
      display: grid;
      grid-template-columns: 12px minmax(0, 1fr) auto;
      align-items: center;
      gap: 6px;
      width: 100%;
      border: 0;
      background: transparent;
      color: #94a3b8;
      cursor: pointer;
      padding: 0;
      text-align: left;
    }
    .proj__head:hover { color: #cbd5e1; }
    .proj__chev {
      width: 6px;
      height: 6px;
      border-right: 1.5px solid #64748b;
      border-bottom: 1.5px solid #64748b;
      transform: rotate(-45deg);
      transition: transform 0.14s ease, border-color 0.14s ease;
      justify-self: center;
    }
    .proj__head:hover .proj__chev { border-color: #cbd5e1; }
    .proj__chev--open { transform: rotate(45deg); }
    .proj__name {
      font-size: 12px;
      color: #94a3b8;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .proj__meta { font-size: 10px; color: #64748b; }
    .proj__summary {
      display: flex;
      gap: 8px;
      padding: 3px 0 0 18px;
      color: #64748b;
      font-size: 10px;
    }
    .sess-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
    .sess {
      padding: 6px 8px;
      border-radius: 8px;
      cursor: pointer;
      background: rgba(255,255,255,0.02);
      border: 1px solid rgba(255,255,255,0.04);
      font-size: 12px;
    }
    .sess:hover { background: rgba(255,255,255,0.05); }
    .sess--active {
      background: rgba(99,102,241,0.18);
      border-color: rgba(99,102,241,0.4);
    }
    .sess__row { display: flex; justify-content: space-between; gap: 8px; }
    .sess__id { font-family: var(--font-mono, monospace); }
    .sess__time { color: #64748b; font-size: 10px; }
    .sess__usage { color: #cdd6f4; font-size: 11px; margin-top: 2px; }
    .sess__usage--muted { color: #64748b; font-style: italic; }

    .sheet__detail {
      border-top: 1px solid rgba(255,255,255,0.06);
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 8px;
      max-height: 50vh;
    }
    .sheet__detail-head { font-size: 12px; color: #cbd5e1; }
    .sheet__detail-usage {
      display: flex;
      gap: 14px;
      font-size: 11px;
      color: #94a3b8;
      flex-wrap: wrap;
    }
    .sheet__detail-usage > div { display: flex; flex-direction: column; gap: 1px; }
    .sheet__detail-usage span:last-child { color: #cdd6f4; font-family: var(--font-mono, monospace); }
    .sheet__detail-empty {
      color: #64748b;
      font-size: 11px;
      line-height: 1.4;
      padding: 6px 8px;
      border-radius: 8px;
      background: rgba(255,255,255,0.03);
    }
  `],
  host: {
    '[class.is-open]': 'open()'
  }
})
export class CliUsageSheetComponent implements OnInit, OnDestroy {
  readonly open = signal(false);
  readonly report = signal<CliUsageReport | null>(null);
  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly selected = signal<SelectedSession | null>(null);
  // Per-CLI accordion state inside the Sessions segment.
  readonly collapsedClis = signal<Set<string>>(new Set());
  readonly expandedProjects = signal<Set<string>>(new Set());
  // Top-level segment accordion state. Quota stays expanded by default
  // because it's the highest-value glance information.
  readonly collapsedSegments = signal<Set<string>>(new Set());

  readonly visibleSections = computed<CliUsageSection[]>(() => {
    const all = this.report()?.sections ?? [];
    return all.filter(s => s.available || s.projects.length > 0);
  });
  readonly totalSessionCount = computed<number>(() =>
    this.visibleSections().reduce((sum, s) => sum + this.totalSessions(s), 0)
  );
  readonly detailLines = computed<CliOutputLine[]>(() => {
    const sel = this.selected();
    if (!sel) return [];
    return [
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `Session: ${sel.session.id}` },
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `CLI:     ${sel.cliType}` },
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `Project: ${sel.projectName}` },
      { timestamp: new Date().toISOString(), stream: 'stdout', text: sel.session.cwd ? `Cwd:     ${sel.session.cwd}` : '' }
    ].filter(l => !!l.text);
  });

  closed = { emit: () => this.open.set(false) };
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  constructor(private jobService: JobService) {}

  show() { this.open.set(true); this.refreshSessions(); }
  hide() { this.open.set(false); }
  toggle() { this.open() ? this.hide() : this.show(); }

  ngOnInit() {
    // Cycle-2 perf: /api/cli/usage walks every CLI's session history on
    // disk; each call costs ~2 s of backend CPU. CLI sessions don't change
    // every 15 s (minutes-to-hours scale), so polling that fast paid 13 %
    // backend CPU continuously while the sheet was open for no real-time
    // benefit. Three guards now:
    //   - 60 s base interval (still picks up new sessions within a minute)
    //   - skip when document.hidden (other tab / minimised window)
    //   - existing guards: only when sheet open AND sessions segment open
    //     AND not already loading
    this.refreshTimer = setInterval(() => {
      if (typeof document !== 'undefined' && document.hidden) return;
      if (this.open() && !this.loading() && !this.isSegmentCollapsed('sessions')) {
        this.refreshSessions(true);
      }
    }, 60_000);
  }

  ngOnDestroy() {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
  }

  refreshSessions(silent = false) {
    if (!silent) this.loading.set(true);
    this.errorMsg.set(null);
    this.jobService.getCliUsageReport().subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: (err) => {
        this.errorMsg.set(err.error?.error || err.message || 'Failed to load CLI usage');
        this.loading.set(false);
      }
    });
  }

  toggleSegment(name: 'quota' | 'sessions') {
    const next = new Set(this.collapsedSegments());
    if (next.has(name)) next.delete(name);
    else next.add(name);
    this.collapsedSegments.set(next);
  }
  isSegmentCollapsed(name: string): boolean { return this.collapsedSegments().has(name); }

  toggleCli(cliType: string) {
    const next = new Set(this.collapsedClis());
    if (next.has(cliType)) next.delete(cliType);
    else next.add(cliType);
    this.collapsedClis.set(next);
  }
  isCliCollapsed(cliType: string): boolean { return this.collapsedClis().has(cliType); }

  toggleProject(cliType: string, project: CliUsageProjectGroup) {
    const key = this.projectKey(cliType, project);
    const next = new Set(this.expandedProjects());
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedProjects.set(next);
  }

  isProjectCollapsed(cliType: string, project: CliUsageProjectGroup): boolean {
    return !this.expandedProjects().has(this.projectKey(cliType, project));
  }

  private projectKey(cliType: string, project: CliUsageProjectGroup): string {
    return `${cliType}:${project.rootPath || project.projectName}`;
  }

  totalSessions(section: CliUsageSection): number {
    return section.projects.reduce((sum, p) => sum + p.sessions.length, 0);
  }

  latestSession(project: CliUsageProjectGroup): CliSessionInfo | null {
    return project.sessions[0] ?? null;
  }

  usageSessionCount(project: CliUsageProjectGroup): number {
    return project.sessions.filter(s => !!s.lastUsage).length;
  }

  select(cliType: CliType, project: CliUsageProjectGroup, session: CliSessionInfo) {
    this.selected.set({ cliType, projectName: project.projectName, session });
  }

  isSelected(cliType: CliType, project: CliUsageProjectGroup, session: CliSessionInfo): boolean {
    const sel = this.selected();
    return !!sel && sel.cliType === cliType && sel.projectName === project.projectName && sel.session.id === session.id;
  }

  cliLabel(t: string): string {
    switch (t) {
      case 'copilot': return 'Copilot';
      case 'claude':  return 'Claude Code';
      case 'codex':   return 'Codex';
      case 'gemini':  return 'Gemini';
      default:        return t;
    }
  }

  shortId(id: string): string {
    return id.length <= 12 ? id : id.slice(0, 8) + '…';
  }

  formatTime(iso: string): string {
    try { return new Date(iso).toLocaleString([], { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }); }
    catch { return iso; }
  }
}
