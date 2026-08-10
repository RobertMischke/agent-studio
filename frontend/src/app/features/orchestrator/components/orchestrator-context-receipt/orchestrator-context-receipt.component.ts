import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import type { OrchestratorContextReceipt, OrchestratorContextSourceReceipt } from '../../models/orchestrator.model';

@Component({
  selector: 'app-orchestrator-context-receipt',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-context-receipt.component.html',
  styleUrl: './orchestrator-context-receipt.component.scss',
})
export class OrchestratorContextReceiptComponent {
  readonly receipt = input.required<OrchestratorContextReceipt>();
  readonly expanded = signal(false);
  readonly sources = computed(() => this.receipt().sources ?? []);
  readonly sourceCount = computed(() => this.sources().length || this.receipt().includedBlocks.length);
  readonly estimatedTokens = computed(() => this.receipt().budget?.estimatedIncludedTokens ?? null);

  toggle(): void {
    this.expanded.update(value => !value);
  }

  kindLabel(kind: string): string {
    return kind.replaceAll('-', ' ').replace(/\b\w/g, value => value.toUpperCase());
  }

  statusLabel(source: OrchestratorContextSourceReceipt): string {
    switch (source.status) {
      case 'included': return 'Included';
      case 'excerpted': return 'Excerpted';
      case 'unresolved': return 'Unresolved';
      case 'unavailable': return 'Unavailable';
      case 'blocked': return 'Blocked';
      case 'oversize': return 'Too large';
      case 'omitted-budget': return 'Excluded by budget';
      default: return this.kindLabel(source.status);
    }
  }

  shortRevision(revision: string | null | undefined): string | null {
    if (!revision) return null;
    return revision.length > 12 ? revision.slice(0, 12) : revision;
  }
}
