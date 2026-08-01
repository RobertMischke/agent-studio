import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskService } from '../../../../services/task.service';
import { NowTickService } from '../../../../services/now-tick.service';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { formatRelativeTime, formatDateTime } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import type {
  CliSessionDetail,
  CliUsageReport,
  CliUsageSection,
  LinkedJobRef,
} from '../../models/cli.model';
import {
  buildRows,
  countByCli,
  filterRows,
  formatSize,
  shortId,
  sortRows,
  tailPath,
  taskChipTone,
  type CliFilter,
  type SessionRow,
  type SessionSortKey,
} from './cli-session-row.util';

/**
 * CLI-session tool: a searchable, filterable, virtualised inventory of every
 * native CLI session on disk (Claude / Codex / Gemini), with lazy per-session
 * detail and guarded cleanup. Embedded in the Workspace-settings CLI-Management
 * home.
 *
 * Performance (AGT-2096 / AGT-1913): a real machine holds thousands of
 * transcripts, so the flattened row list is rendered through a CDK
 * fixed-size virtual viewport — only the visible window is in the DOM. The list
 * report itself never reads transcript bodies; model / thinking / message-count
 * come from a lazy `session-detail` fetch fired only when a row is opened.
 *
 * Theming: every colour reads a `--studio-*` token so both themes stay legible
 * (R5); no hard-coded hex.
 */
@Component({
  selector: 'app-cli-sessions-panel',
  standalone: true,
  imports: [FormsModule, ScrollingModule, TooltipDirective],
  templateUrl: './cli-sessions-panel.html',
  styleUrl: './cli-sessions-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliSessionsPanelComponent implements OnInit {
  private readonly jobService = inject(TaskService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly nowTick = inject(NowTickService);

  /** Opens the owning task's detail when a session's task chip is activated. */
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  readonly report = signal<CliUsageReport | null>(null);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly errorMsg = signal<string | null>(null);

  // Toolbar state.
  readonly query = signal('');
  readonly cliFilter = signal<CliFilter>('all');
  readonly sortKey = signal<SessionSortKey>('recent');
  readonly linkedOnly = signal(false);

  // Detail aside state (lazy).
  readonly selected = signal<SessionRow | null>(null);
  readonly detail = signal<CliSessionDetail | null>(null);
  readonly detailLoading = signal(false);
  readonly detailError = signal<string | null>(null);
  readonly busyRow = signal<string | null>(null);
  readonly flash = signal<string | null>(null);

  readonly now = this.nowTick.now;

  readonly allRows = computed<SessionRow[]>(() => buildRows(this.report()));
  readonly cliCounts = computed<Record<string, number>>(() => countByCli(this.allRows()));
  readonly availableClis = computed<CliType[]>(() => {
    const seen = new Set<CliType>();
    for (const s of this.report()?.sections ?? []) {
      if (s.available || s.projects.length > 0) seen.add(s.cliType as CliType);
    }
    return [...seen];
  });
  readonly visibleRows = computed<SessionRow[]>(() =>
    sortRows(
      filterRows(this.allRows(), this.cliFilter(), this.query(), this.linkedOnly()),
      this.sortKey(),
    ),
  );
  readonly totalCount = computed(() => this.allRows().length);
  readonly shownCount = computed(() => this.visibleRows().length);
  readonly sectionErrors = computed<CliUsageSection[]>(() =>
    (this.report()?.sections ?? []).filter((s) => !!s.error),
  );

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.errorMsg.set(null);
    this.jobService.getCliUsageReport().subscribe({
      next: (r) => {
        this.report.set(r);
        this.loaded.set(true);
        this.loading.set(false);
        // Drop a stale selection whose row no longer exists.
        const sel = this.selected();
        if (sel && !this.allRows().some((row) => row.key === sel.key)) this.clearSelection();
      },
      error: (err) => {
        this.errorMsg.set(err.error?.error || err.message || 'Failed to load CLI sessions');
        this.loaded.set(true);
        this.loading.set(false);
      },
    });
  }

  setCliFilter(cli: CliFilter): void {
    this.cliFilter.set(cli);
  }

  setSort(key: SessionSortKey): void {
    this.sortKey.set(key);
  }

  toggleLinkedOnly(): void {
    this.linkedOnly.update((v) => !v);
  }

  clearQuery(): void {
    this.query.set('');
  }

  trackRow = (_: number, row: SessionRow): string => row.key;

  select(row: SessionRow): void {
    this.selected.set(row);
    this.loadDetail(row);
  }

  isSelected(row: SessionRow): boolean {
    return this.selected()?.key === row.key;
  }

  clearSelection(): void {
    this.selected.set(null);
    this.detail.set(null);
    this.detailError.set(null);
  }

  private loadDetail(row: SessionRow): void {
    this.detail.set(null);
    this.detailError.set(null);
    this.detailLoading.set(true);
    this.jobService.getCliSessionDetail(row.cliType, row.id, row.rootPath).subscribe({
      next: (d) => {
        this.detail.set(d);
        if (d.error) this.detailError.set(d.error);
        this.detailLoading.set(false);
      },
      error: (err) => {
        this.detailError.set(err.error?.error || err.message || 'Detail unavailable');
        this.detailLoading.set(false);
      },
    });
  }

  // ── Actions ──────────────────────────────────────────────────────────

  openTask(event: Event, link: LinkedJobRef): void {
    event.stopPropagation();
    this.openJobDetail.emit({ jobId: link.jobId, watchPath: link.watchPath });
  }

  async copy(text: string | null, label: string): Promise<void> {
    if (!text) return;
    const ok = await copyTextToClipboard(text);
    this.flashMsg(ok ? `${label} copied` : `Could not copy ${label.toLowerCase()}`);
  }

  async cleanup(row: SessionRow): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Delete this CLI session?',
      message:
        'This removes the on-disk transcript for this session. The linked task, if any, keeps its record; only the raw CLI transcript is deleted.',
      detail: `${row.cliLabel} · ${row.label || shortId(row.id)}\n${row.rootPath ?? ''}`,
      confirmLabel: 'Delete session',
      kind: 'danger',
    });
    if (!ok) return;

    this.busyRow.set(row.key);
    this.jobService.deleteCliSession(row.cliType, row.id, row.rootPath).subscribe({
      next: (res) => {
        this.busyRow.set(null);
        if (res.status === 'Deleted') {
          this.flashMsg('Session deleted');
          if (this.isSelected(row)) this.clearSelection();
          this.refresh();
        } else {
          this.flashMsg(res.message || 'Delete refused');
        }
      },
      error: (err) => {
        this.busyRow.set(null);
        this.flashMsg(err.error?.message || err.error?.error || 'Delete failed');
      },
    });
  }

  private flashMsg(msg: string): void {
    this.flash.set(msg);
    setTimeout(() => {
      if (this.flash() === msg) this.flash.set(null);
    }, 2500);
  }

  // ── Formatting (thin wrappers over the pure utils) ───────────────────

  relative(iso: string | null): string {
    return iso ? formatRelativeTime(iso, this.now()) : '';
  }
  absolute(iso: string | null): string {
    return iso ? formatDateTime(iso) : '';
  }
  size(bytes: number): string {
    return formatSize(bytes);
  }
  short(id: string): string {
    return shortId(id);
  }
  tail(path: string | null): string {
    return tailPath(path);
  }
  chipTone(link: LinkedJobRef): string {
    return taskChipTone(link.lane, link.isActive);
  }
  chipTooltip(link: LinkedJobRef): string {
    return `Open task: ${link.title} — ${link.isActive ? 'active' : link.lane}`;
  }
}
