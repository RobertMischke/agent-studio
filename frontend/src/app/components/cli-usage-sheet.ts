import { Component, OnDestroy, OnInit, signal, computed } from '@angular/core';
import { JobService } from '../services/job.service';
import { CliConsoleComponent } from './cli-console';
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
 * Global right side-sheet listing every known CLI session across all three
 * backends. Sessions are grouped per CLI → per project; selecting a row shows
 * its last known token usage and (when available) recent output via the
 * existing TTY component.
 *
 * The actual session output streaming for Claude and Codex is best-effort:
 * we surface what's already on disk; live tail isn't wired yet.
 */
@Component({
  selector: 'app-cli-usage-sheet',
  standalone: true,
  imports: [CliConsoleComponent],
  template: `
    <aside class="sheet" [class.sheet--open]="open()">
      <header class="sheet__header">
        <div>
          <h2 class="sheet__title">CLI Sessions</h2>
          <div class="sheet__subtitle">Live output and token usage per session</div>
        </div>
        <div class="sheet__header-actions">
          <button class="sheet__btn" (click)="refresh()" [disabled]="loading()">
            {{ loading() ? '⏳' : '↻' }} Refresh
          </button>
          <button class="sheet__close" type="button" (click)="closed.emit()">✕</button>
        </div>
      </header>

      @if (errorMsg(); as err) {
        <div class="sheet__error">{{ err }}</div>
      }

      <div class="sheet__body">
        @for (section of visibleSections(); track section.cliType) {
          <section class="sheet-section">
            <button class="sheet-section__head"
                    type="button"
                    (click)="toggleSection(section.cliType)">
              <span class="sheet-section__chev">{{ isCollapsed(section.cliType) ? '▶' : '▼' }}</span>
              <span class="sheet-section__title">{{ cliLabel(section.cliType) }}</span>
              @if (section.version) {
                <span class="sheet-pill sheet-pill--ok">{{ section.version }}</span>
              }
              <span class="sheet-section__count">
                {{ totalSessions(section) }} sessions
              </span>
            </button>

            @if (!isCollapsed(section.cliType)) {
              @if (section.error) {
                <div class="sheet__error sheet__error--inline">{{ section.error }}</div>
              }
              @if (section.projects.length === 0) {
                <div class="sheet__empty">No sessions found yet for {{ cliLabel(section.cliType) }}.</div>
              }
              @for (project of section.projects; track project.projectName) {
                <div class="sheet-project">
                  <div class="sheet-project__name" [title]="project.rootPath || ''">
                    📁 {{ project.projectName }}
                  </div>
                  <ul class="sheet-sessions">
                    @for (s of project.sessions; track s.id) {
                      <li class="sheet-session"
                          [class.sheet-session--active]="isSelected(section.cliType, project, s)"
                          (click)="select(section.cliType, project, s)">
                        <div class="sheet-session__row">
                          <span class="sheet-session__id" [title]="s.id">
                            {{ s.label || shortId(s.id) }}
                          </span>
                          @if (s.updatedAt) {
                            <span class="sheet-session__time">{{ formatTime(s.updatedAt) }}</span>
                          }
                        </div>
                        @if (s.lastUsage?.tokens) {
                          <div class="sheet-session__usage">
                            🪙 {{ s.lastUsage?.tokens }}
                          </div>
                        }
                      </li>
                    }
                  </ul>
                </div>
              }
            }
          </section>
        }
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
          }
          <app-cli-console [lines]="detailLines()" [title]="'Session ' + shortId(sel.session.id)" [bodyMaxHeight]="'30vh'" />
        </div>
      }
    </aside>
  `,
  styles: [`
    :host { display: contents; }
    .sheet {
      position: fixed;
      top: 0;
      right: 0;
      bottom: 0;
      width: min(440px, 92vw);
      background: #11111b;
      border-left: 1px solid rgba(255,255,255,0.08);
      box-shadow: -8px 0 32px rgba(0,0,0,0.45);
      transform: translateX(100%);
      transition: transform 0.22s ease;
      z-index: 90;
      display: flex;
      flex-direction: column;
      color: #e2e8f0;
    }
    .sheet--open { transform: translateX(0); }
    .sheet__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      padding: 16px 18px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .sheet__subtitle {
      font-size: 12px;
      color: #64748b;
      margin-top: 2px;
    }
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
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .sheet-section {
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 12px;
      background: rgba(255,255,255,0.02);
      padding: 10px;
    }
    .sheet-section__head {
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
    .sheet-section__chev { font-size: 10px; color: #64748b; width: 10px; }
    .sheet-section__title { font-weight: 600; }
    .sheet-section__count {
      margin-left: auto;
      font-size: 11px;
      color: #64748b;
    }
    .sheet-pill {
      font-size: 10px;
      padding: 2px 6px;
      border-radius: 999px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .sheet-pill--ok { background: rgba(34,197,94,0.12); color: #4ade80; }
    .sheet-pill--err { background: rgba(244,63,94,0.12); color: #fda4af; }
    .sheet__empty {
      padding: 8px 4px;
      color: #64748b;
      font-size: 12px;
    }
    .sheet-project {
      margin-top: 8px;
      padding-top: 8px;
      border-top: 1px dashed rgba(255,255,255,0.06);
    }
    .sheet-project__name {
      font-size: 12px;
      color: #94a3b8;
      margin-bottom: 4px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .sheet-sessions { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
    .sheet-session {
      padding: 6px 8px;
      border-radius: 8px;
      cursor: pointer;
      background: rgba(255,255,255,0.02);
      border: 1px solid rgba(255,255,255,0.04);
      font-size: 12px;
    }
    .sheet-session:hover { background: rgba(255,255,255,0.05); }
    .sheet-session--active {
      background: rgba(99,102,241,0.18);
      border-color: rgba(99,102,241,0.4);
    }
    .sheet-session__row { display: flex; justify-content: space-between; gap: 8px; }
    .sheet-session__id { font-family: var(--font-mono, monospace); }
    .sheet-session__time { color: #64748b; font-size: 10px; }
    .sheet-session__usage { color: #cdd6f4; font-size: 11px; margin-top: 2px; }
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
  `]
})
export class CliUsageSheetComponent implements OnInit, OnDestroy {
  readonly open = signal(false);
  readonly report = signal<CliUsageReport | null>(null);
  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly selected = signal<SelectedSession | null>(null);
  readonly collapsedSections = signal<Set<string>>(new Set());
  /**
   * Sections we surface in the UI. CLIs that aren't installed/available on the
   * host produce empty sections that would just show an "unavailable" badge —
   * we hide them entirely so the sheet stays focused on what's actually usable.
   */
  readonly visibleSections = computed<CliUsageSection[]>(() => {
    const all = this.report()?.sections ?? [];
    return all.filter(s => s.available || s.projects.length > 0);
  });
  readonly detailLines = computed<CliOutputLine[]>(() => {
    const sel = this.selected();
    if (!sel) return [];
    // Lightweight placeholder content for the TTY view — real per-session output
    // streaming can be wired in a follow-up. We at least show the metadata so
    // the panel isn't empty.
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

  show() { this.open.set(true); this.refresh(); }
  hide() { this.open.set(false); }
  toggle() { this.open() ? this.hide() : this.show(); }

  ngOnInit() {
    // Auto-refresh while open
    this.refreshTimer = setInterval(() => {
      if (this.open() && !this.loading()) this.refresh(true);
    }, 15000);
  }

  ngOnDestroy() {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
  }

  refresh(silent = false) {
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

  toggleSection(cliType: string) {
    const next = new Set(this.collapsedSections());
    if (next.has(cliType)) next.delete(cliType);
    else next.add(cliType);
    this.collapsedSections.set(next);
  }

  isCollapsed(cliType: string): boolean {
    return this.collapsedSections().has(cliType);
  }

  totalSessions(section: CliUsageSection): number {
    return section.projects.reduce((sum, p) => sum + p.sessions.length, 0);
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
