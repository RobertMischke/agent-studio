import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  inject,
  signal,
} from '@angular/core';
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
  templateUrl: './code-pattern-drift-card.html',
  styleUrl: './code-pattern-drift-card.scss',
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
      next: (r) => this.rules.set(r ?? []),
      error: () => this.rules.set([]),
    });
  }

  run(): void {
    this.running.set(true);
    this.error.set(null);
    this.drift.runCodePatternDrift().subscribe({
      next: (response) => {
        this.report.set(response.report);
        this.running.set(false);
      },
      error: (err) => {
        this.error.set(err?.message ?? 'unknown error');
        this.running.set(false);
      },
    });
  }

  toggleRules(): void {
    this.rulesVisible.update((v) => !v);
  }

  driftHits(finding: CodePatternFinding) {
    return finding.hits.filter((h) => h.isDrift);
  }

  trackById(_: number, finding: CodePatternFinding) {
    return finding.ruleId;
  }

  overallBand(report: CodePatternDriftReport): 'ok' | 'warn' | 'high' | 'critical' {
    let max = 0;
    for (const f of report.findings) {
      const v =
        ({ Info: 0, Warn: 1, High: 2, Critical: 3 } as Record<string, number>)[f.overallSeverity] ??
        0;
      if (v > max) max = v;
    }
    if (max >= 3) return 'critical';
    if (max >= 2) return 'high';
    if (max >= 1) return 'warn';
    return 'ok';
  }
}
