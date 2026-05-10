import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { OrchestratorSession } from '../../../models/job.model';
import { JobService } from '../../../services/job.service';
import { ConceptHelpComponent } from '../../../components/concept-help/concept-help.component';

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
  imports: [ConceptHelpComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="global-orch" data-testid="global-orchestrator-card">
      <header class="global-orch__head">
        <span class="global-orch__role">Global orchestrator</span>
        <app-concept-help concept="orchestrator" />
        <span class="global-orch__scope">Across all watched projects</span>
      </header>

      @if (session(); as s) {
        <p class="global-orch__voice" [title]="s.bootPromptPreview">
          “{{ s.bootReplyPreview || 'Booted, no reply captured.' }}”
        </p>
        <dl class="global-orch__meta">
          <div>
            <dt>Model</dt>
            <dd>{{ s.model }}</dd>
          </div>
          <div>
            <dt>Booted</dt>
            <dd>{{ formatTime(s.bootedAt) }} · {{ s.calls }} call{{ s.calls === 1 ? '' : 's' }} so far</dd>
          </div>
          <div>
            <dt>Talk to it</dt>
            <dd><code>claude -r {{ s.sessionId }}</code></dd>
          </div>
          @if (s.lastError) {
            <div>
              <dt>Last error</dt>
              <dd class="global-orch__error">{{ s.lastError }}</dd>
            </div>
          }
        </dl>
      } @else if (loading()) {
        <p class="global-orch__loading">Reading the global orchestrator's state…</p>
      } @else {
        <p class="global-orch__empty">
          No global session booted yet. The app boots one Claude session that knows about every watched
          project at startup. If this stays empty for more than a few seconds, the boot probably failed.
        </p>
      }
    </article>
  `,
  styles: [`
    :host { display: block; margin: 0 0 24px; }

    .global-orch {
      padding: 18px 22px;
      border: 1px solid rgba(196, 181, 253, 0.22);
      border-radius: 12px;
      background:
        radial-gradient(circle at top left, rgba(139, 92, 246, 0.12), transparent 60%),
        rgba(15, 23, 42, 0.45);
    }
    .global-orch__head {
      display: flex;
      align-items: baseline;
      gap: 12px;
      margin-bottom: 12px;
    }
    .global-orch__role {
      font-size: 1.0rem;
      font-weight: 700;
      color: #e9d5ff;
      letter-spacing: 0.01em;
    }
    .global-orch__scope {
      font-size: 0.82rem;
      color: rgba(255,255,255,0.55);
    }

    .global-orch__voice {
      margin: 0 0 16px;
      font-size: 1.0rem;
      line-height: 1.55;
      color: #f1f5f9;
      font-style: italic;
      cursor: help;
    }

    .global-orch__meta {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 6px 16px;
      margin: 0;
      font-size: 0.86rem;
    }
    .global-orch__meta > div { display: contents; }
    .global-orch__meta dt {
      color: rgba(255,255,255,0.50);
    }
    .global-orch__meta dd {
      margin: 0;
      color: #e2e8f0;
      font-family: var(--font-mono, monospace);
      word-break: break-all;
    }
    .global-orch__error { color: #fda4af; }

    .global-orch__loading,
    .global-orch__empty {
      margin: 0;
      color: rgba(255,255,255,0.55);
      font-size: 0.88rem;
      line-height: 1.5;
    }
  `]
})
export class GlobalOrchestratorCardComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(JobService);
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
