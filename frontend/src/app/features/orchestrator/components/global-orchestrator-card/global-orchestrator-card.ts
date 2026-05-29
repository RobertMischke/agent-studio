import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import type { OrchestratorSession } from '../../../../features/orchestrator';
import { TaskService } from '../../../../services/task.service';
import { ConceptHelpComponent } from '../../../../components/concept-help/concept-help.component';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Global orchestrator card. Sits above the per-project orchestrator panel
 * and surfaces the singleton session that lives across all projects:
 * what it knows about the board, when it last spoke, and a hint for the
 * user to talk to it directly via `claude -r <id>`. Read-only today.
 *
 * Visual language: leads with the boot reply (the orchestrator's own
 * voice in plain text, no uppercase chrome), followed by metadata in
 * a softer block. The point of the redesign is to make the panel
 * feel like reading a colleague's note, not a status dashboard.
 */
@Component({
  selector: 'app-global-orchestrator-card',
  standalone: true,
  imports: [ConceptHelpComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './global-orchestrator-card.html',
  styleUrl: './global-orchestrator-card.scss'
})
export class GlobalOrchestratorCardComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(TaskService);
  readonly session = signal<OrchestratorSession | null>(null);
  readonly loading = signal(true);
  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    // Slow poll: the global session changes only at boot or on rare resume
    // calls, so we don't need a tight tick. 60s keeps the panel honest
    // without burning HTTP traffic.
    this.timer = setInterval(() => this.refresh(), 60_000);
  }

  ngOnDestroy(): void {
    if (this.timer != null) clearInterval(this.timer);
    this.timer = null;
  }

  private refresh(): void {
    this.jobService.getGlobalOrchestratorSession().subscribe({
      next: (resp) => {
        this.session.set(resp.session ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
      }
    });
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }
}
