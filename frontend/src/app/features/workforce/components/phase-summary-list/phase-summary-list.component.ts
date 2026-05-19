import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RoleBadgeComponent } from '../role-badge/role-badge.component';
import type { ChatPhase } from '../../models/chat-phase';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Compressed summary layer rendered above the verbatim chat. Each row
 * is one phase (a contiguous block of messages anchored by a user
 * steer); clicking the row toggles the expansion state for that phase
 * so the host can decide what to do with the underlying messages.
 *
 * Default-expanded behaviour: the most recent phase starts expanded;
 * everything before it starts collapsed. The host can drive the state
 * explicitly via the `expandedPhaseIds` input; when null we manage the
 * state internally and only emit `phaseToggled` for observability.
 */
@Component({
  selector: 'app-phase-summary-list',
  standalone: true,
  imports: [CommonModule, RoleBadgeComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './phase-summary-list.component.html',
  styleUrl: './phase-summary-list.component.scss',
})
export class PhaseSummaryListComponent {
  readonly phases = input<readonly ChatPhase[]>([]);
  /**
   * Host-driven expansion state. When `null`, the component manages it
   * internally and starts with only the last phase expanded.
   */
  readonly expandedPhaseIds = input<ReadonlySet<string> | null>(null);
  /**
   * Compact mode collapses the entire list to one "▸ N earlier phases"
   * strip until the user clicks it. Stops the summary from filling the
   * chat panel with rows of historical timestamps when the user just
   * wants to see the active conversation. The last phase (always
   * expanded by default) is still hidden from the strip header so it
   * does not double up with the verbatim chat below.
   */
  readonly compact = input<boolean>(false);

  readonly phaseToggled = output<{ phaseId: string; expanded: boolean }>();

  /** When true and compact, the user has opened the full phase list. */
  readonly compactRevealed = signal<boolean>(false);

  /** Phases that are "earlier" than the active last phase (compact mode). */
  readonly earlierPhases = computed<readonly ChatPhase[]>(() => {
    const all = this.phases();
    return all.length <= 1 ? [] : all.slice(0, -1);
  });

  /** Range string spanning all earlier phases — shown in the compact header. */
  readonly earlierRange = computed<string>(() => {
    const earlier = this.earlierPhases();
    if (earlier.length === 0) return '';
    const first = earlier[0];
    const last = earlier[earlier.length - 1];
    const start = first.startTs;
    const end = last.endTs ?? last.startTs;
    if (!start) return '';
    if (start === end) return formatTs(start);
    return `${formatTs(start)} → ${formatTs(end)}`;
  });

  toggleCompactRevealed(): void {
    this.compactRevealed.update(v => !v);
  }

  private readonly internalExpanded = signal<Set<string>>(new Set());

  readonly effectiveExpanded = computed<ReadonlySet<string>>(() => {
    const host = this.expandedPhaseIds();
    if (host) return host;
    const internal = this.internalExpanded();
    if (internal.size > 0) return internal;
    // Default: only the most recent phase expanded.
    const all = this.phases();
    if (all.length === 0) return new Set();
    return new Set([all[all.length - 1].id]);
  });

  isExpanded(phaseId: string): boolean {
    return this.effectiveExpanded().has(phaseId);
  }

  togglePhase(phaseId: string): void {
    const host = this.expandedPhaseIds();
    if (!host) {
      const next = new Set(this.effectiveExpanded());
      const willExpand = !next.has(phaseId);
      if (willExpand) {
        next.add(phaseId);
      } else {
        next.delete(phaseId);
      }
      this.internalExpanded.set(next);
      this.phaseToggled.emit({ phaseId, expanded: willExpand });
      return;
    }
    this.phaseToggled.emit({ phaseId, expanded: !host.has(phaseId) });
  }

  formatTimeRange(phase: ChatPhase): string {
    return formatPhaseRange(phase);
  }
}

export function formatPhaseRange(phase: ChatPhase): string {
  if (!phase.startTs) return '';
  if (phase.startTs === phase.endTs) return formatTs(phase.startTs);
  return `${formatTs(phase.startTs)} → ${formatTs(phase.endTs)}`;
}

function formatTs(iso: string): string {
  try {
    return new Date(iso).toLocaleString(undefined, {
      month: 'short',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    });
  } catch {
    return iso;
  }
}
