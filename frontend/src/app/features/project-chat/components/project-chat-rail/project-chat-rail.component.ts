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
import type { ProjectChatTurn } from '../../../../features/project-chat';

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
  templateUrl: './project-chat-rail.component.html',
  styleUrl: './project-chat-rail.component.scss',
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
