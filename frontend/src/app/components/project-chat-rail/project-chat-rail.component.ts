import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  ViewChild,
  computed,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { ProjectChatTurn } from '../../models/job.model';

/**
 * Slice C right-rail. A narrow column (~22 px) painted next to the
 * virtualised chat that mirrors the conversation as a minimap. Each
 * non-trivial turn or embedded event becomes a small chip at the same
 * vertical fraction as its source row; chips that would visually
 * overlap collapse into a cluster with a count badge.
 *
 * The rail is a *view* over the existing chat data — it never edits or
 * reorders the chat, only emits {@link chipSelect} when the user picks
 * a chip so the host can smooth-scroll the chat list and (for legacy
 * Slice A collapsed turns) expand the body. Click semantics live in
 * the parent so the rail stays presentational and easy to test.
 *
 * Position model: chips are absolutely positioned on a fraction of the
 * rail's pixel height — `top = ((i + 0.5) / turns.length) * railH`.
 * Clustering happens after the per-chip pixel position is known so the
 * collision threshold matches the pixel size of the chip glyphs.
 */
export type RailChipKind = 'long' | 'event' | 'error' | 'running';

export interface RailChip {
  /** Index into the host's `turns()` array. Drives `top` and click target. */
  sourceIndex: number;
  /** Pinned for click scroll-to-turn handoff. */
  turnId: string;
  kind: RailChipKind;
  /** First-line preview, capped at 80 chars, plain text. */
  preview: string;
  /** N for `▼ N more` chips so the chip can show the count. */
  longMoreLines?: number;
}

interface RailCluster {
  /** Top pixel relative to the rail. */
  topPx: number;
  /** Members ordered by source index. */
  members: RailChip[];
  /** Severity-of-cluster glyph: error wins, then event, then long, then running. */
  glyphKind: RailChipKind;
  /** Stable id for trackBy + expanded-state lookup. */
  id: string;
}

const CLUSTER_PX = 14; // collision radius — slightly above chip height
const CHIP_HEIGHT_PX = 12;

@Component({
  selector: 'app-project-chat-rail',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      #rail
      class="rail"
      data-testid="project-chat-rail"
      [attr.data-density]="density()">
      <!-- Visible viewport band: a subtle highlight showing what slice
           of the conversation is in view in the chat scroll column. -->
      @if (turns().length > 0) {
        <div
          class="rail__viewport"
          data-testid="pchat-rail-viewport"
          [style.top.%]="viewportTopPct()"
          [style.height.%]="viewportHeightPct()"></div>
      }

      @for (cluster of clusters(); track cluster.id) {
        <button
          type="button"
          class="rail__chip"
          [class.rail__chip--cluster]="!isSingle(cluster)"
          [class.rail__chip--in-viewport]="clusterInViewport(cluster)"
          [class.rail__chip--long]="cluster.glyphKind === 'long'"
          [class.rail__chip--event]="cluster.glyphKind === 'event'"
          [class.rail__chip--error]="cluster.glyphKind === 'error'"
          [class.rail__chip--running]="cluster.glyphKind === 'running'"
          [class.rail__chip--expanded]="expandedId() === cluster.id"
          [attr.data-testid]="isSingle(cluster) ? 'pchat-rail-chip' : 'pchat-rail-cluster'"
          [attr.data-kind]="cluster.glyphKind"
          [attr.data-count]="cluster.members.length"
          [attr.aria-label]="ariaFor(cluster)"
          [attr.title]="titleFor(cluster)"
          [style.top.px]="cluster.topPx"
          (click)="onClusterClick($event, cluster)"
          (keydown.enter)="onClusterClick($event, cluster)"
          (keydown.space)="onClusterClick($event, cluster)">
          <span class="rail__glyph">{{ glyphFor(cluster.glyphKind) }}</span>
          @if (!isSingle(cluster)) {
            <span class="rail__badge">{{ cluster.members.length }}</span>
          } @else if (cluster.glyphKind === 'long' && cluster.members[0].longMoreLines) {
            <span class="rail__badge rail__badge--long">{{ cluster.members[0].longMoreLines }}</span>
          }
        </button>

        @if (expandedId() === cluster.id && !isSingle(cluster)) {
          <div
            class="rail__menu"
            data-testid="pchat-rail-cluster-menu"
            [style.top.px]="cluster.topPx + 14"
            (click)="$event.stopPropagation()">
            @for (member of cluster.members; track member.turnId) {
              <button
                type="button"
                class="rail__menu-item"
                [class.rail__menu-item--long]="member.kind === 'long'"
                [class.rail__menu-item--event]="member.kind === 'event'"
                [class.rail__menu-item--error]="member.kind === 'error'"
                [class.rail__menu-item--running]="member.kind === 'running'"
                [attr.data-testid]="'pchat-rail-cluster-item'"
                [attr.data-turnid]="member.turnId"
                (click)="onMemberClick(member)">
                <span class="rail__menu-glyph">{{ glyphFor(member.kind) }}</span>
                <span class="rail__menu-text">{{ member.preview || '(no preview)' }}</span>
              </button>
            }
          </div>
        }
      }
    </div>
  `,
  styles: [`
    :host {
      display: block;
      flex: 0 0 22px;
      width: 22px;
      height: 100%;
      min-height: 0;
    }
    .rail {
      position: relative;
      width: 100%;
      height: 100%;
      background: rgba(255,255,255,0.015);
      border-left: 1px solid rgba(255,255,255,0.04);
      overflow: visible;
    }
    .rail__viewport {
      position: absolute;
      left: 2px;
      right: 2px;
      background: rgba(196,181,253,0.10);
      border-radius: 2px;
      pointer-events: none;
      transition: top 120ms ease, height 120ms ease;
    }
    .rail__chip {
      position: absolute;
      left: 50%;
      transform: translate(-50%, -50%);
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 2px;
      width: 14px;
      min-width: 14px;
      height: 12px;
      padding: 0 2px;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 4px;
      background: rgba(30,30,46,0.85);
      color: #cdd6f4;
      font-family: ui-monospace, SFMono-Regular, monospace;
      font-size: 9px;
      line-height: 1;
      cursor: pointer;
      opacity: 0.65;
      transition: opacity 120ms ease, transform 120ms ease, border-color 120ms ease;
    }
    .rail__chip:hover,
    .rail__chip:focus-visible {
      opacity: 1;
      outline: none;
      border-color: rgba(196,181,253,0.7);
      transform: translate(-50%, -50%) scale(1.18);
      z-index: 2;
    }
    .rail__chip--in-viewport {
      opacity: 0.95;
      border-color: rgba(196,181,253,0.55);
    }
    .rail__chip--long       { color: #94e2d5; }   /* teal */
    .rail__chip--event      { color: #cba6f7; }   /* mauve */
    .rail__chip--error      { color: #f38ba8; border-color: rgba(243,139,168,0.55); }
    .rail__chip--running    { color: #f9e2af; }   /* yellow */
    .rail__chip--cluster {
      width: auto;
      min-width: 18px;
      padding: 0 3px;
      background: rgba(49,50,68,0.95);
    }
    .rail__chip--expanded {
      opacity: 1;
      border-color: rgba(196,181,253,0.9);
    }
    .rail__glyph { font-size: 9px; line-height: 1; }
    .rail__badge {
      font-size: 8px;
      color: #94a3b8;
      font-variant-numeric: tabular-nums;
    }
    .rail__badge--long { color: #94e2d5; }
    .rail__menu {
      position: absolute;
      right: 24px;          /* sit just left of the rail column */
      min-width: 220px;
      max-width: 320px;
      background: rgba(24,24,37,0.97);
      border: 1px solid rgba(196,181,253,0.4);
      border-radius: 6px;
      box-shadow: 0 8px 24px rgba(0,0,0,0.55);
      padding: 4px;
      z-index: 5;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .rail__menu-item {
      display: flex;
      align-items: center;
      gap: 6px;
      width: 100%;
      padding: 4px 6px;
      background: transparent;
      border: 1px solid transparent;
      border-radius: 4px;
      color: #cdd6f4;
      cursor: pointer;
      text-align: left;
      font: inherit;
      font-size: 11px;
      line-height: 1.3;
    }
    .rail__menu-item:hover,
    .rail__menu-item:focus-visible {
      background: rgba(124,58,237,0.18);
      border-color: rgba(196,181,253,0.4);
      outline: none;
    }
    .rail__menu-item--long  .rail__menu-glyph { color: #94e2d5; }
    .rail__menu-item--event .rail__menu-glyph { color: #cba6f7; }
    .rail__menu-item--error .rail__menu-glyph { color: #f38ba8; }
    .rail__menu-item--running .rail__menu-glyph { color: #f9e2af; }
    .rail__menu-glyph {
      flex: 0 0 14px;
      font-family: ui-monospace, SFMono-Regular, monospace;
      font-size: 11px;
      text-align: center;
    }
    .rail__menu-text {
      flex: 1 1 auto;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `],
})
export class ProjectChatRailComponent implements AfterViewInit, OnDestroy {
  /** Source list — same array the virtualised chat renders. */
  readonly turns = input.required<ProjectChatTurn[]>();
  /** Visible window into `turns` (chat virtualisation state). */
  readonly visibleStart = input<number>(0);
  readonly visibleEnd = input<number>(0);
  /** Optional turnId to show as the "currently running" CLI marker. */
  readonly runningTurnId = input<string | null>(null);

  /** Emitted when the user picks a chip / cluster member. */
  readonly chipSelect = output<{ turnId: string }>();

  @ViewChild('rail', { static: true }) railEl!: ElementRef<HTMLDivElement>;

  /** Pixel height of the rail; tracked via ResizeObserver so chip
   *  positions stay accurate while the side sheet animates open / the
   *  user resizes the window. */
  readonly railHeightPx = signal(0);
  readonly expandedId = signal<string | null>(null);

  private resizeObserver: ResizeObserver | null = null;
  private outsideClickHandler: ((e: MouseEvent) => void) | null = null;

  /** Chips for every "non-trivial" turn / event in `turns`. Kept as a
   *  pure derivation so re-renders are cheap. */
  readonly chips = computed<RailChip[]>(() => {
    const all = this.turns();
    const running = this.runningTurnId();
    const out: RailChip[] = [];
    for (let i = 0; i < all.length; i++) {
      const t = all[i];
      const kind = chipKindFor(t, running);
      if (!kind) continue;
      out.push({
        sourceIndex: i,
        turnId: t.turnId,
        kind,
        preview: previewFor(t),
        longMoreLines: kind === 'long' ? countLines(t.body) : undefined,
      });
    }
    return out;
  });

  /** Cluster chips whose pixel positions are within {@link CLUSTER_PX}.
   *  When the rail has zero height we still emit single-member clusters
   *  so the host renders something on first paint; positions get
   *  recomputed once the ResizeObserver fires. */
  readonly clusters = computed<RailCluster[]>(() => {
    const cs = this.chips();
    const total = this.turns().length;
    const railH = Math.max(1, this.railHeightPx());
    if (cs.length === 0 || total === 0) return [];

    const clusters: RailCluster[] = [];
    for (const chip of cs) {
      const top = ((chip.sourceIndex + 0.5) / total) * railH;
      const last = clusters.at(-1);
      if (last && Math.abs(top - last.topPx) <= CLUSTER_PX) {
        last.members.push(chip);
        // Recentre cluster on the average position of its members for
        // visual stability as members accumulate.
        const sum = last.members.reduce(
          (acc, m) => acc + ((m.sourceIndex + 0.5) / total) * railH,
          0,
        );
        last.topPx = sum / last.members.length;
        last.glyphKind = pickGlyphKind(last.members);
        last.id = `c-${last.members[0].turnId}-${last.members.length}`;
      } else {
        clusters.push({
          topPx: top,
          members: [chip],
          glyphKind: chip.kind,
          id: `c-${chip.turnId}-1`,
        });
      }
    }
    return clusters;
  });

  readonly density = computed<'empty' | 'low' | 'mid' | 'high'>(() => {
    const n = this.chips().length;
    if (n === 0) return 'empty';
    if (n < 6) return 'low';
    if (n < 25) return 'mid';
    return 'high';
  });

  readonly viewportTopPct = computed(() => {
    const n = this.turns().length;
    if (n === 0) return 0;
    return Math.max(0, Math.min(100, (this.visibleStart() / n) * 100));
  });

  readonly viewportHeightPct = computed(() => {
    const n = this.turns().length;
    if (n === 0) return 0;
    const span = Math.max(0, this.visibleEnd() - this.visibleStart());
    return Math.max(2, Math.min(100, (span / n) * 100));
  });

  constructor() {
    // Collapse any expanded cluster when the source list changes — a
    // turn appearing or disappearing under the menu can otherwise leave
    // a dangling popover pointing at a now-wrong cluster id.
    effect(() => {
      void this.chips();
      this.expandedId.set(null);
    });
  }

  ngAfterViewInit(): void {
    const el = this.railEl.nativeElement;
    // Initial measurement so chips paint at the right `top` on first
    // tick (before any resize happens).
    this.railHeightPx.set(el.clientHeight);

    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver((entries) => {
        for (const entry of entries) {
          const h = entry.contentRect.height;
          if (h > 0 && h !== this.railHeightPx()) this.railHeightPx.set(h);
        }
      });
      this.resizeObserver.observe(el);
    }

    this.outsideClickHandler = (e: MouseEvent) => {
      if (!this.expandedId()) return;
      const target = e.target as Node | null;
      if (target && this.railEl.nativeElement.contains(target)) return;
      this.expandedId.set(null);
    };
    document.addEventListener('mousedown', this.outsideClickHandler);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    if (this.outsideClickHandler) {
      document.removeEventListener('mousedown', this.outsideClickHandler);
      this.outsideClickHandler = null;
    }
  }

  isSingle(c: RailCluster): boolean { return c.members.length === 1; }

  clusterInViewport(c: RailCluster): boolean {
    const start = this.visibleStart();
    const end = this.visibleEnd();
    return c.members.some((m) => m.sourceIndex >= start && m.sourceIndex < end);
  }

  glyphFor(kind: RailChipKind): string {
    switch (kind) {
      case 'long':    return '▼';
      case 'event':   return '⚙';
      case 'error':   return '🐞';
      case 'running': return '⚡';
    }
  }

  ariaFor(c: RailCluster): string {
    if (c.members.length === 1) {
      const m = c.members[0];
      return `${kindLabel(m.kind)}: ${m.preview || '(no preview)'}`;
    }
    return `${c.members.length} chat markers near this position`;
  }

  titleFor(c: RailCluster): string {
    if (c.members.length === 1) return c.members[0].preview || kindLabel(c.members[0].kind);
    const lines = c.members
      .slice(0, 6)
      .map((m) => `${this.glyphFor(m.kind)} ${m.preview}`);
    if (c.members.length > 6) lines.push(`… +${c.members.length - 6} more`);
    return lines.join('\n');
  }

  onClusterClick(event: Event, cluster: RailCluster): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.isSingle(cluster)) {
      const m = cluster.members[0];
      this.expandedId.set(null);
      this.chipSelect.emit({ turnId: m.turnId });
      return;
    }
    // Toggle the stacked menu on a cluster click.
    this.expandedId.set(this.expandedId() === cluster.id ? null : cluster.id);
  }

  onMemberClick(member: RailChip): void {
    this.expandedId.set(null);
    this.chipSelect.emit({ turnId: member.turnId });
  }
}

/* ───────────────────────── helpers ─────────────────────────────── */

function chipKindFor(t: ProjectChatTurn, runningTurnId: string | null): RailChipKind | null {
  if (runningTurnId && t.turnId === runningTurnId) return 'running';
  if (t.kind === 'event-watchdog' || t.kind === 'event-rate-limit') return 'error';
  if (t.kind && t.kind.startsWith('event-')) return 'event';
  if (t.kind === 'turn') {
    const lines = countLines(t.body);
    if (lines >= 10 || (t.body?.length ?? 0) > 800) return 'long';
  }
  return null;
}

function countLines(body: string): number {
  if (!body) return 0;
  // A turn that wraps onto many soft lines isn't necessarily "long",
  // but the row height is fixed at 120 px so any body with ≥10 hard
  // lines is guaranteed to overflow into Slice A's collapsed state in
  // the legacy chat renderer. Match that as the threshold.
  return body.split('\n').length;
}

function previewFor(t: ProjectChatTurn): string {
  // Strip markdown punctuation that adds noise without information.
  const raw = (t.body || '').replace(/[`*_>#\[\]()!]/g, ' ').replace(/\s+/g, ' ').trim();
  if (raw.length <= 80) return raw;
  return raw.slice(0, 77) + '…';
}

function kindLabel(kind: RailChipKind): string {
  switch (kind) {
    case 'long':    return 'Long turn';
    case 'event':   return 'Event';
    case 'error':   return 'Error event';
    case 'running': return 'Running CLI';
  }
}

function pickGlyphKind(members: RailChip[]): RailChipKind {
  // Severity priority: error > running > event > long.
  if (members.some((m) => m.kind === 'error')) return 'error';
  if (members.some((m) => m.kind === 'running')) return 'running';
  if (members.some((m) => m.kind === 'event')) return 'event';
  return 'long';
}
