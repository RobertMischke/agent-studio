import {
  ChangeDetectionStrategy,
  Component,
  HostListener,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { JobScreenshot } from '../../models/job.model';
import { copyTextToClipboard } from '../../services/clipboard.util';

/**
 * Visual-evidence strip + lightbox. Two surfaces share this component:
 *
 * 1. The per-task strip in the protocol pane, populated from
 *    `GET /api/jobs/{id}/screenshots` (every image under
 *    `<job>/results/`, including the harvested Playwright artefacts).
 * 2. The workspace "Visual evidence" reel, populated from
 *    `GET /api/workspace/screenshots` and grouped by hour bucket on
 *    the rendering side; entries are clickable thumbnails that link
 *    back to the originating task page.
 *
 * The strip itself is only rendered when at least one screenshot is
 * present; this is enforced at the host site (no in-component "empty
 * state" placeholder, per the task contract).
 *
 * Lightbox is owned here. Prev/next navigation cycles deterministically
 * (wraps at both ends) so the keyboard shortcut `←` / `→` is always
 * meaningful. `Esc` closes. The "Open in Explorer" affordance copies
 * the absolute on-disk path to the clipboard since the browser cannot
 * launch Explorer directly.
 */
@Component({
  selector: 'app-screenshot-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="strip" data-testid="screenshot-strip"
         [attr.data-variant]="variant()"
         [attr.aria-label]="ariaLabel()">
      <div class="strip__rail" role="list">
        @for (s of screenshots(); track s.url; let i = $index) {
          <button type="button"
                  role="listitem"
                  class="strip__thumb"
                  [attr.data-testid]="'screenshot-thumb'"
                  [attr.data-index]="i"
                  [attr.data-status]="s.status ?? 'none'"
                  [title]="thumbTitle(s)"
                  (click)="openLightbox(i)">
            <img class="strip__img" [src]="s.url" [alt]="s.caption" loading="lazy" />
            @if (s.status === 'passed') {
              <span class="strip__badge strip__badge--ok" aria-label="Passed">✓</span>
            } @else if (s.status === 'failed') {
              <span class="strip__badge strip__badge--bad" aria-label="Failed">✗</span>
            } @else if (s.status === 'skipped') {
              <span class="strip__badge strip__badge--skip" aria-label="Skipped">⊘</span>
            }
            <span class="strip__caption">
              <span class="strip__caption-spec">{{ s.caption }}</span>
              <span class="strip__caption-ts">{{ formatTimestamp(s.timestampUtc) }}</span>
              @if (variant() === 'reel' && s.projectName) {
                <span class="strip__caption-project">{{ s.projectName }}</span>
              }
            </span>
          </button>
        }
      </div>
    </div>

    @if (active() !== null) {
      <div class="lightbox"
           data-testid="screenshot-lightbox"
           role="dialog"
           aria-modal="true"
           (click)="close()">
        <button type="button" class="lightbox__close"
                data-testid="screenshot-lightbox-close"
                (click)="close(); $event.stopPropagation()"
                aria-label="Close">×</button>
        @if (current(); as c) {
          <button type="button" class="lightbox__nav lightbox__nav--prev"
                  data-testid="screenshot-lightbox-prev"
                  (click)="prev(); $event.stopPropagation()"
                  aria-label="Previous">‹</button>
          <figure class="lightbox__figure" (click)="$event.stopPropagation()">
            <img class="lightbox__img" [src]="c.url" [alt]="c.caption"
                 data-testid="screenshot-lightbox-image" />
            <figcaption class="lightbox__caption">
              <div class="lightbox__caption-main">
                <span class="lightbox__caption-spec" data-testid="screenshot-lightbox-caption">{{ c.caption }}</span>
                @if (c.status) {
                  <span class="lightbox__status"
                        [attr.data-status]="c.status">{{ statusLabel(c.status) }}</span>
                }
                <span class="lightbox__index" data-testid="screenshot-lightbox-index">
                  {{ activeIndex() + 1 }} / {{ screenshots().length }}
                </span>
              </div>
              <div class="lightbox__caption-meta">
                <span>{{ formatTimestamp(c.timestampUtc) }}</span>
                @if (c.projectName) {
                  <span>·</span>
                  <span>{{ c.projectName }}</span>
                }
                @if (c.jobTitle && variant() === 'reel') {
                  <span>·</span>
                  <a class="lightbox__task-link"
                     data-testid="screenshot-lightbox-open-task"
                     href="javascript:void(0)"
                     (click)="onOpenTask(c); $event.stopPropagation()">
                    Open task
                  </a>
                }
                <span>·</span>
                <button type="button" class="lightbox__path-btn"
                        data-testid="screenshot-lightbox-open-explorer"
                        (click)="copyLocalPath(c); $event.stopPropagation()"
                        [title]="c.localPath">
                  {{ pathButtonLabel() }}
                </button>
              </div>
            </figcaption>
          </figure>
          <button type="button" class="lightbox__nav lightbox__nav--next"
                  data-testid="screenshot-lightbox-next"
                  (click)="next(); $event.stopPropagation()"
                  aria-label="Next">›</button>
        }
      </div>
    }
  `,
  styleUrl: './screenshot-strip.component.scss'
})
export class ScreenshotStripComponent {
  readonly screenshots = input.required<JobScreenshot[]>();
  /** 'task' = per-task strip in protocol pane; 'reel' = workspace reel. */
  readonly variant = input<'task' | 'reel'>('task');

  readonly openTask = output<JobScreenshot>();

  readonly activeIndex = signal<number>(-1);

  readonly active = computed(() =>
    this.activeIndex() >= 0 && this.activeIndex() < this.screenshots().length
      ? this.activeIndex()
      : null
  );

  readonly current = computed<JobScreenshot | null>(() => {
    const i = this.activeIndex();
    if (i < 0) return null;
    const all = this.screenshots();
    return i < all.length ? all[i] : null;
  });

  readonly ariaLabel = computed(() =>
    this.variant() === 'reel' ? 'Workspace visual evidence reel' : 'Task screenshots'
  );

  readonly pathCopyState = signal<'idle' | 'copied' | 'failed'>('idle');

  pathButtonLabel(): string {
    const s = this.pathCopyState();
    if (s === 'copied') return 'Path copied';
    if (s === 'failed') return 'Copy failed';
    return 'Open in Explorer';
  }

  openLightbox(index: number): void {
    this.activeIndex.set(index);
  }

  close(): void {
    this.activeIndex.set(-1);
    this.pathCopyState.set('idle');
  }

  prev(): void {
    const total = this.screenshots().length;
    if (total === 0) return;
    const cur = this.activeIndex();
    this.activeIndex.set((cur - 1 + total) % total);
    this.pathCopyState.set('idle');
  }

  next(): void {
    const total = this.screenshots().length;
    if (total === 0) return;
    const cur = this.activeIndex();
    this.activeIndex.set((cur + 1) % total);
    this.pathCopyState.set('idle');
  }

  thumbTitle(s: JobScreenshot): string {
    const ts = formatLocalDateTime(s.timestampUtc);
    const lines = [s.caption, s.fileName, ts];
    if (s.projectName) lines.push(s.projectName);
    if (s.status) lines.push(`Status: ${this.statusLabel(s.status)}`);
    return lines.filter(Boolean).join('\n');
  }

  statusLabel(status: string): string {
    switch (status) {
      case 'passed':  return 'Passed';
      case 'failed':  return 'Failed';
      case 'skipped': return 'Skipped';
      default:        return 'Unknown';
    }
  }

  formatTimestamp(iso: string): string {
    return formatLocalDateTime(iso);
  }

  async copyLocalPath(s: JobScreenshot): Promise<void> {
    if (!s.localPath) return;
    const ok = await copyTextToClipboard(s.localPath);
    this.pathCopyState.set(ok ? 'copied' : 'failed');
    setTimeout(() => this.pathCopyState.set('idle'), 1800);
  }

  onOpenTask(s: JobScreenshot): void {
    this.openTask.emit(s);
  }

  @HostListener('window:keydown', ['$event'])
  onKey(e: KeyboardEvent): void {
    if (this.activeIndex() < 0) return;
    if (e.key === 'Escape') { this.close(); e.preventDefault(); }
    else if (e.key === 'ArrowLeft') { this.prev(); e.preventDefault(); }
    else if (e.key === 'ArrowRight') { this.next(); e.preventDefault(); }
  }
}

function formatLocalDateTime(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  // Locale-neutral compact form: YYYY-MM-DD HH:mm
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} `
       + `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
