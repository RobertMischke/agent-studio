import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  CodePatternDriftReport,
  CodePatternFinding,
  CodePatternRuleSummary,
  DriftService,
} from '../../../../services/drift.service';

/**
 * Project-screen card for the **Code Pattern Drift** quality gate. Hits
 * `POST /api/drift/actions/code-pattern-drift` and renders the per-rule
 * findings inline.
 *
 * <p>The detector is deterministic and fast (no LLM call); we surface a
 * one-click "Run check" button and render the report below it. Drift sites
 * link back to their file:line so the reviewer can drill straight in.</p>
 *
 * <p>Empty state (zero findings) is the goal — that means every site
 * matches the canonical pattern. The card stays visible because the rule
 * list itself is informative: it shows what the gate watches for.</p>
 */
@Component({
  selector: 'app-code-pattern-drift-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <section class="cpd-card">
      <header class="cpd-card__head">
        <h3>Code pattern drift</h3>
        <span class="cpd-card__sub">Deterministic quality gate &mdash; catches code-pattern divergence before it ships</span>
      </header>

      <div class="cpd-card__actions">
        <button class="cpd-btn" (click)="run()" [disabled]="running()">
          {{ running() ? 'Running…' : 'Run check' }}
        </button>
        <button class="cpd-btn cpd-btn--secondary" (click)="toggleRules()">
          {{ rulesVisible() ? 'Hide rules' : 'Show rules' }}
        </button>
      </div>

      <div class="cpd-card__rules" *ngIf="rulesVisible()">
        <h4>Active rules ({{ rules().length }})</h4>
        <ul>
          <li *ngFor="let rule of rules()">
            <code>{{ rule.id }}</code>
            <span class="cpd-sev cpd-sev--{{ rule.severity.toLowerCase() }}">{{ rule.severity }}</span>
            <span class="cpd-rule__title">{{ rule.title }}</span>
            <p class="cpd-rule__desc">{{ rule.canonicalDescription }}</p>
          </li>
        </ul>
      </div>

      <div class="cpd-card__error" *ngIf="error() as e">
        Failed to run drift check: {{ e }}
      </div>

      <div class="cpd-card__report" *ngIf="report() as r">
        <div class="cpd-summary">
          <span class="cpd-summary__total">
            <strong>{{ r.totalDriftSites }}</strong> drift site{{ r.totalDriftSites === 1 ? '' : 's' }}
          </span>
          <span class="cpd-summary__band cpd-summary__band--{{ overallBand(r) }}">
            {{ overallBand(r).toUpperCase() }}
          </span>
          <span class="cpd-summary__at">at {{ r.capturedAt | date:'short' }}</span>
        </div>

        <article class="cpd-finding" *ngFor="let finding of r.findings; trackBy: trackById">
          <header class="cpd-finding__head">
            <h4>{{ finding.title }}</h4>
            <span class="cpd-sev cpd-sev--{{ finding.overallSeverity.toLowerCase() }}">
              {{ finding.overallSeverity }}
            </span>
          </header>
          <p class="cpd-finding__desc">{{ finding.canonicalDescription }}</p>
          <p class="cpd-finding__stats">
            Total sites: <strong>{{ finding.totalSites }}</strong>
            (canonical: {{ finding.canonicalSites }}, drift: {{ finding.driftSites }})
          </p>

          <div *ngIf="driftHits(finding).length > 0" class="cpd-finding__hits">
            <h5>Drift sites</h5>
            <ul>
              <li *ngFor="let hit of driftHits(finding)">
                <code>{{ hit.filePath }}:{{ hit.lineNumber }}</code>
                <span class="cpd-hit__evidence">{{ hit.evidence }}</span>
              </li>
            </ul>
          </div>
          <div *ngIf="driftHits(finding).length === 0" class="cpd-finding__clean">
            ✓ No drift detected — all {{ finding.totalSites }} sites match the canonical pattern.
          </div>
        </article>
      </div>

      <div class="cpd-card__empty" *ngIf="!report() && !running() && !error()">
        Click <em>Run check</em> to scan the repository for code-pattern drift.
      </div>
    </section>
  `,
  styles: [`
    .cpd-card {
      background: var(--surface, #1a1d27);
      border: 1px solid var(--border, #2e3347);
      border-radius: 8px;
      padding: 1rem 1.25rem;
      margin-bottom: 1rem;
    }
    .cpd-card__head h3 {
      margin: 0;
      font-size: 1.05rem;
    }
    .cpd-card__sub {
      font-size: 0.78rem;
      color: var(--muted, #8892a4);
    }
    .cpd-card__actions {
      display: flex;
      gap: 0.5rem;
      margin: 0.6rem 0 0.8rem;
    }
    .cpd-btn {
      padding: 0.4rem 0.8rem;
      border-radius: 4px;
      border: 1px solid var(--accent, #6366f1);
      background: var(--accent, #6366f1);
      color: #fff;
      cursor: pointer;
      font-size: 0.85rem;
    }
    .cpd-btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .cpd-btn--secondary {
      background: transparent;
      color: var(--text, #e2e8f0);
    }
    .cpd-card__rules { margin: 0.4rem 0 1rem; font-size: 0.82rem; }
    .cpd-card__rules h4 { margin: 0 0 0.4rem; font-size: 0.85rem; }
    .cpd-card__rules ul { list-style: none; padding-left: 0; margin: 0; }
    .cpd-card__rules li { padding: 0.3rem 0; border-bottom: 1px dashed var(--border, #2e3347); }
    .cpd-rule__title { font-weight: 600; margin-left: 0.4rem; }
    .cpd-rule__desc { color: var(--muted, #8892a4); margin: 0.2rem 0 0; font-size: 0.78rem; }
    .cpd-sev {
      display: inline-block;
      padding: 0.1em 0.45em;
      border-radius: 3px;
      font-size: 0.7rem;
      font-weight: 600;
      margin-left: 0.4rem;
    }
    .cpd-sev--info { background: rgba(96,165,250,.15); color: #60a5fa; }
    .cpd-sev--warn { background: rgba(251,191,36,.15); color: #fbbf24; }
    .cpd-sev--high { background: rgba(248,113,113,.15); color: #f87171; }
    .cpd-sev--critical { background: rgba(248,113,113,.25); color: #fca5a5; }
    .cpd-card__error {
      padding: 0.5rem 0.7rem;
      background: rgba(248,113,113,.1);
      border: 1px solid rgba(248,113,113,.3);
      border-radius: 4px;
      color: #f87171;
      margin: 0.5rem 0;
      font-size: 0.85rem;
    }
    .cpd-summary {
      display: flex;
      gap: 1rem;
      align-items: center;
      margin: 0.5rem 0 1rem;
      font-size: 0.9rem;
    }
    .cpd-summary__band {
      padding: 0.15em 0.6em;
      border-radius: 4px;
      font-size: 0.75rem;
      font-weight: 700;
    }
    .cpd-summary__band--ok { background: rgba(52,211,153,.15); color: #34d399; }
    .cpd-summary__band--warn { background: rgba(251,191,36,.15); color: #fbbf24; }
    .cpd-summary__band--high { background: rgba(248,113,113,.15); color: #f87171; }
    .cpd-summary__band--critical { background: rgba(248,113,113,.25); color: #fca5a5; }
    .cpd-summary__at { color: var(--muted, #8892a4); font-size: 0.78rem; }
    .cpd-finding {
      border: 1px solid var(--border, #2e3347);
      border-radius: 6px;
      padding: 0.6rem 0.8rem;
      margin-bottom: 0.6rem;
    }
    .cpd-finding__head {
      display: flex;
      align-items: center;
      gap: 0.4rem;
    }
    .cpd-finding__head h4 { margin: 0; font-size: 0.92rem; }
    .cpd-finding__desc { color: var(--muted, #8892a4); margin: 0.3rem 0; font-size: 0.8rem; }
    .cpd-finding__stats { margin: 0.3rem 0; font-size: 0.82rem; }
    .cpd-finding__hits ul { list-style: none; padding-left: 0; margin: 0.3rem 0 0; }
    .cpd-finding__hits li { padding: 0.2rem 0; font-size: 0.82rem; }
    .cpd-hit__evidence {
      margin-left: 0.5rem;
      font-style: italic;
      color: var(--muted, #8892a4);
      font-size: 0.75rem;
    }
    .cpd-finding__clean {
      margin-top: 0.3rem;
      color: #34d399;
      font-size: 0.82rem;
    }
    .cpd-card__empty {
      color: var(--muted, #8892a4);
      font-size: 0.85rem;
      padding: 0.5rem 0;
    }
  `],
})
export class CodePatternDriftCardComponent implements OnInit {
  private readonly drift = inject(DriftService);

  readonly running = signal(false);
  readonly error = signal<string | null>(null);
  readonly report = signal<CodePatternDriftReport | null>(null);
  readonly rules = signal<CodePatternRuleSummary[]>([]);
  readonly rulesVisible = signal(false);

  ngOnInit(): void {
    this.drift.getCodePatternRules().subscribe({
      next: r => this.rules.set(r ?? []),
      error: () => this.rules.set([]),
    });
  }

  run(): void {
    this.running.set(true);
    this.error.set(null);
    this.drift.runCodePatternDrift().subscribe({
      next: response => {
        this.report.set(response.report);
        this.running.set(false);
      },
      error: err => {
        this.error.set(err?.message ?? 'unknown error');
        this.running.set(false);
      },
    });
  }

  toggleRules(): void {
    this.rulesVisible.update(v => !v);
  }

  driftHits(finding: CodePatternFinding) {
    return finding.hits.filter(h => h.isDrift);
  }

  trackById(_: number, finding: CodePatternFinding) {
    return finding.ruleId;
  }

  overallBand(report: CodePatternDriftReport): 'ok' | 'warn' | 'high' | 'critical' {
    let max = 0;
    for (const f of report.findings) {
      const v = ({ Info: 0, Warn: 1, High: 2, Critical: 3 } as Record<string, number>)[f.overallSeverity] ?? 0;
      if (v > max) max = v;
    }
    if (max >= 3) return 'critical';
    if (max >= 2) return 'high';
    if (max >= 1) return 'warn';
    return 'ok';
  }
}
