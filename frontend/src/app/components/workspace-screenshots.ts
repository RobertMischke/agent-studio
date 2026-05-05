import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  output,
  signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { JobService } from '../services/job.service';
import { JobScreenshot } from '../models/job.model';
import { ScreenshotStripComponent } from './screenshot-strip/screenshot-strip.component';

interface HourBucket {
  bucketKey: string;
  bucketLabel: string;
  items: JobScreenshot[];
}

const STORAGE_WINDOW_KEY = 'workspaceScreenshots.windowHours';
const STORAGE_PROJECT_KEY = 'workspaceScreenshots.project';

const WINDOW_OPTIONS: { hours: number; label: string; testId: string }[] = [
  { hours: 24,  label: '24 h', testId: '24h' },
  { hours: 72,  label: '3 days', testId: '3d' },
  { hours: 168, label: '7 days', testId: '7d' }
];

/**
 * Workspace-wide visual evidence reel. Folds every recent screenshot
 * across all watched projects and groups them into hour buckets so a
 * burst of activity stays visually together. Each thumbnail opens the
 * shared lightbox; the lightbox's "Open task" link navigates to the
 * originating job page so the reel is a discovery surface, not a
 * dead-end gallery.
 *
 * The endpoint behind this is `GET /api/workspace/screenshots`.
 * Project filter and window selection live in localStorage so the
 * user's last view is preserved across sessions.
 */
@Component({
  selector: 'app-workspace-screenshots',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, ScreenshotStripComponent],
  template: `
    <section class="wss" data-testid="workspace-screenshots">
      <header class="wss__head">
        <div>
          <h2 class="wss__title">Visual evidence</h2>
          <p class="wss__sub">
            Recent screenshots harvested from every watched project.
            @if (loaded() && entries().length > 0) {
              <span class="wss__sub-stats">
                · {{ entries().length }} image{{ entries().length === 1 ? '' : 's' }}
                across {{ projectCount() }} project{{ projectCount() === 1 ? '' : 's' }}
              </span>
            }
          </p>
        </div>
        <div class="wss__controls">
          <div class="wss__win" role="radiogroup" aria-label="Window">
            @for (opt of windowOptions; track opt.hours) {
              <button type="button"
                      class="wss__win-btn"
                      [class.wss__win-btn--active]="windowHours() === opt.hours"
                      [attr.data-testid]="'wss-win-' + opt.testId"
                      role="radio"
                      [attr.aria-checked]="windowHours() === opt.hours"
                      (click)="setWindow(opt.hours)">
                {{ opt.label }}
              </button>
            }
          </div>
          @if (projects().length > 1) {
            <select class="wss__project"
                    data-testid="wss-project-filter"
                    [value]="projectFilter() ?? ''"
                    (change)="setProject($any($event.target).value)">
              <option value="">All projects</option>
              @for (p of projects(); track p) {
                <option [value]="p">{{ p }}</option>
              }
            </select>
          }
          <button type="button"
                  class="wss__refresh"
                  data-testid="wss-refresh"
                  [disabled]="loading()"
                  (click)="refresh()">
            {{ loading() ? '⏳' : '↻' }}
          </button>
        </div>
      </header>

      @if (loading() && entries().length === 0) {
        <div class="wss__placeholder" data-testid="wss-loading">Loading screenshots...</div>
      } @else if (loaded() && entries().length === 0) {
        <div class="wss__placeholder" data-testid="wss-empty">
          No screenshots produced inside the selected window.
        </div>
      } @else {
        <div class="wss__buckets">
          @for (b of buckets(); track b.bucketKey) {
            <section class="wss__bucket"
                     [attr.data-testid]="'wss-bucket'"
                     [attr.data-bucket-key]="b.bucketKey">
              <header class="wss__bucket-head">
                <h3 class="wss__bucket-title">{{ b.bucketLabel }}</h3>
                <span class="wss__bucket-count">{{ b.items.length }} image{{ b.items.length === 1 ? '' : 's' }}</span>
              </header>
              <app-screenshot-strip
                [screenshots]="b.items"
                variant="reel"
                (openTask)="onOpenTask($event)" />
            </section>
          }
        </div>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }

    .wss {
      display: flex;
      flex-direction: column;
      gap: 16px;
      padding: 16px;
      color: #cdd6f4;
    }

    .wss__head {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 12px;
      flex-wrap: wrap;
    }

    .wss__title {
      margin: 0 0 4px 0;
      font-size: 1.25rem;
    }

    .wss__sub {
      margin: 0;
      color: #a6adc8;
      font-size: 0.92rem;
    }

    .wss__sub-stats { color: #cdd6f4; }

    .wss__controls {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }

    .wss__win {
      display: inline-flex;
      gap: 0;
      border: 1px solid rgba(180, 190, 254, 0.3);
      border-radius: 6px;
      overflow: hidden;
    }

    .wss__win-btn {
      background: transparent;
      border: 0;
      color: #cdd6f4;
      font: inherit;
      font-size: 0.84rem;
      padding: 6px 10px;
      cursor: pointer;

      &--active { background: rgba(137, 180, 250, 0.25); }
      &:hover { background: rgba(137, 180, 250, 0.15); }
    }

    .wss__project {
      background: rgba(30, 30, 46, 0.6);
      border: 1px solid rgba(180, 190, 254, 0.3);
      color: #cdd6f4;
      padding: 5px 8px;
      border-radius: 6px;
      font: inherit;
      font-size: 0.84rem;
    }

    .wss__refresh {
      background: rgba(30, 30, 46, 0.6);
      border: 1px solid rgba(180, 190, 254, 0.3);
      color: #cdd6f4;
      padding: 5px 10px;
      border-radius: 6px;
      cursor: pointer;
      font: inherit;
    }

    .wss__placeholder {
      padding: 24px;
      text-align: center;
      color: #a6adc8;
      border: 1px dashed rgba(180, 190, 254, 0.25);
      border-radius: 8px;
    }

    .wss__buckets {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }

    .wss__bucket {
      border: 1px solid rgba(180, 190, 254, 0.18);
      border-radius: 8px;
      background: rgba(17, 17, 27, 0.5);
      padding: 6px 8px 8px;
    }

    .wss__bucket-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 4px 4px 6px;
    }

    .wss__bucket-title {
      margin: 0;
      font-size: 0.95rem;
      color: #cdd6f4;
      font-variant-numeric: tabular-nums;
    }

    .wss__bucket-count {
      color: #a6adc8;
      font-size: 0.82rem;
    }
  `]
})
export class WorkspaceScreenshotsComponent implements OnInit, OnDestroy {
  readonly openTask = output<JobScreenshot>();

  readonly windowOptions = WINDOW_OPTIONS;

  readonly windowHours = signal<number>(this.loadWindow());
  readonly projectFilter = signal<string | null>(this.loadProject());
  readonly entries = signal<JobScreenshot[]>([]);
  readonly loading = signal<boolean>(false);
  readonly loaded = signal<boolean>(false);

  readonly projects = computed(() => {
    const set = new Set<string>();
    for (const e of this.entries()) {
      if (e.projectName) set.add(e.projectName);
    }
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  });

  readonly projectCount = computed(() => this.projects().length);

  readonly buckets = computed<HourBucket[]>(() => {
    const items = this.entries();
    if (items.length === 0) return [];
    const byKey = new Map<string, HourBucket>();
    for (const it of items) {
      const d = new Date(it.timestampUtc);
      const key = bucketKey(d);
      const label = bucketLabel(d);
      let b = byKey.get(key);
      if (!b) {
        b = { bucketKey: key, bucketLabel: label, items: [] };
        byKey.set(key, b);
      }
      b.items.push(it);
    }
    // Sort buckets newest-first; entries inside each bucket are already newest-first.
    return Array.from(byKey.values()).sort((a, b) => b.bucketKey.localeCompare(a.bucketKey));
  });

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(private readonly jobs: JobService) {}

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(true), 30_000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  setWindow(hours: number): void {
    if (this.windowHours() === hours) return;
    this.windowHours.set(hours);
    try { localStorage.setItem(STORAGE_WINDOW_KEY, String(hours)); } catch { /* ignore */ }
    this.refresh();
  }

  setProject(value: string): void {
    const v = value && value.length > 0 ? value : null;
    this.projectFilter.set(v);
    try {
      if (v) localStorage.setItem(STORAGE_PROJECT_KEY, v);
      else localStorage.removeItem(STORAGE_PROJECT_KEY);
    } catch { /* ignore */ }
    this.refresh();
  }

  refresh(silent: boolean = false): void {
    if (!silent) this.loading.set(true);
    this.jobs.getWorkspaceScreenshots(this.windowHours(), this.projectFilter()).subscribe({
      next: (res) => {
        this.entries.set(res?.screenshots ?? []);
        this.loaded.set(true);
      },
      error: () => { /* keep prior entries */ },
      complete: () => this.loading.set(false)
    });
  }

  onOpenTask(s: JobScreenshot): void {
    this.openTask.emit(s);
  }

  private loadWindow(): number {
    try {
      const raw = localStorage.getItem(STORAGE_WINDOW_KEY);
      const n = raw ? Number(raw) : 72;
      return WINDOW_OPTIONS.some(o => o.hours === n) ? n : 72;
    } catch {
      return 72;
    }
  }

  private loadProject(): string | null {
    try {
      const raw = localStorage.getItem(STORAGE_PROJECT_KEY);
      return raw && raw.length > 0 ? raw : null;
    } catch {
      return null;
    }
  }
}

function bucketKey(d: Date): string {
  // Local-hour bucket key. Newest-first sort uses string compare so the
  // key shape must be lexicographically chronological.
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:00`;
}

function bucketLabel(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:00`;
}
