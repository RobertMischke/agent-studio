import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { AnalysisReportService } from '../../../services/analysis-report.service';
import { AnalysisReport, AnalysisReportReference } from '../../../models/analysis-report.model';

/**
 * Drill-down overlay for one analysis report. Renders the full Markdown body,
 * the structured JSON, and the typed reference list. Markdown is the durable
 * human artifact: it stays visible even when the JSON sidecar is unstructured
 * or malformed (the warning is shown alongside, not in place of, the body).
 *
 * Reference rows do not auto-resolve to deep links in this first cut; they
 * carry the stable id strings (job, run, commit, screenshot path, bus message,
 * runtime event, previous report, log slice, doc) so the user can navigate by
 * hand or copy them into another tool. The Agent Message Bus and runtime
 * surfaces are linked via the existing project-page entry points; this view
 * does not duplicate those timelines.
 */
@Component({
  selector: 'app-analysis-report-drilldown',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="ard" data-testid="analysis-report-drilldown">
      <header class="ard__head">
        <span class="ard__sev ard__sev--{{ (report()?.severity ?? 'Info').toLowerCase() }}">
          {{ report()?.severity ?? 'Info' }}
        </span>
        <h2 class="ard__title">{{ report()?.topic ?? '…' }}</h2>
        <span class="ard__trigger">{{ report()?.trigger ?? '' }}</span>
      </header>

      @if (report()?.parseStatus === 'Unstructured') {
        <div class="ard__warn" data-testid="analysis-report-warn-unstructured">
          ⚠️ <strong>Unstructured report.</strong>
          The producer did not write a JSON sidecar; the Markdown body below is the only artifact.
          Structured filters (severity, follow-ups, references) are not promised for this report.
        </div>
      } @else if (report()?.parseStatus === 'MalformedJson') {
        <div class="ard__warn ard__warn--strong" data-testid="analysis-report-warn-malformed">
          ⚠️ <strong>Malformed JSON sidecar.</strong>
          The Markdown body is still readable below; the sidecar failed to parse:
          <code>{{ report()?.parseError ?? 'unknown' }}</code>
        </div>
      }

      <dl class="ard__meta">
        <div><dt>Report id</dt><dd><code>{{ report()?.reportId }}</code></dd></div>
        <div><dt>Created</dt><dd>{{ formatTime(report()?.createdAt) }}</dd></div>
        <div><dt>Scope</dt><dd>{{ scopeLabel() }}</dd></div>
        <div><dt>Producer</dt><dd>{{ producerLabel() }}</dd></div>
        <div><dt>Parse status</dt><dd>{{ report()?.parseStatus }}</dd></div>
      </dl>

      @if ((report()?.findings?.length ?? 0) > 0) {
        <h3 class="ard__sub">Findings</h3>
        <ul class="ard__findings">
          @for (f of report()!.findings; track f.topic) {
            <li class="ard__finding ard__finding--{{ f.severity.toLowerCase() }}">
              <span class="ard__finding-sev">{{ f.severity }}</span>
              <span class="ard__finding-topic">{{ f.topic }}</span>
              <span class="ard__finding-msg">{{ f.message }}</span>
            </li>
          }
        </ul>
      }

      @if ((report()?.followUpTaskSuggestions?.length ?? 0) > 0) {
        <h3 class="ard__sub">Follow-up suggestions</h3>
        <ul class="ard__followups">
          @for (s of report()!.followUpTaskSuggestions; track s.title) {
            <li>
              <strong>{{ s.title }}</strong>
              <span class="ard__followup-prio">{{ s.priority }}</span>
              <p>{{ s.summary }}</p>
              @if (s.createdJobId) {
                <span class="ard__followup-job">queued as <code>{{ s.createdJobId }}</code></span>
              } @else {
                <span class="ard__followup-job">candidate (not yet queued)</span>
              }
            </li>
          }
        </ul>
      }

      @if ((report()?.references?.length ?? 0) > 0) {
        <h3 class="ard__sub">References</h3>
        <ul class="ard__refs" data-testid="analysis-report-references">
          @for (ref of report()!.references; track ref.ref) {
            <li>
              <span class="ard__ref-kind">{{ ref.kind }}</span>
              <code class="ard__ref-id">{{ ref.ref }}</code>
              @if (ref.label) { <span class="ard__ref-label">{{ ref.label }}</span> }
            </li>
          }
        </ul>
      }

      <h3 class="ard__sub">Markdown</h3>
      @if (markdown()) {
        <pre class="ard__markdown" data-testid="analysis-report-markdown">{{ markdown() }}</pre>
      } @else if (loading()) {
        <p class="ard__empty">Loading report body…</p>
      } @else {
        <p class="ard__empty" data-testid="analysis-report-markdown-missing">
          Markdown body is unavailable for this report. The structured record is shown above.
        </p>
      }

      <h3 class="ard__sub">Structured JSON</h3>
      <pre class="ard__json" data-testid="analysis-report-json">{{ jsonView() }}</pre>

      <div class="ard__actions">
        <button class="ard__btn"
                data-testid="analysis-report-close"
                (click)="close.emit()">Close</button>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; padding: 20px 22px; max-width: 880px; margin: 0 auto; color: #cdd6f4; }
    .ard__head {
      display: flex;
      align-items: baseline;
      gap: 10px;
      margin-bottom: 14px;
      padding-bottom: 8px;
      border-bottom: 1px solid rgba(255,255,255,0.10);
    }
    .ard__title { margin: 0; color: #f8fafc; font-size: 1.1rem; flex: 1; }
    .ard__sev {
      text-transform: uppercase;
      font-weight: 600;
      font-size: 0.70rem;
      padding: 1px 6px;
      border-radius: 3px;
      background: rgba(255,255,255,0.08);
    }
    .ard__sev--info { background: rgba(148,163,184,0.18); color: #cbd5e1; }
    .ard__sev--warn { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .ard__sev--high { background: rgba(250,179,135,0.20); color: #fab387; }
    .ard__sev--critical { background: rgba(243,139,168,0.22); color: #f38ba8; }
    .ard__trigger { color: rgba(255,255,255,0.55); font-size: 0.78rem; }

    .ard__warn {
      margin: 0 0 14px;
      padding: 8px 10px;
      border: 1px solid rgba(249,226,175,0.40);
      border-left-width: 3px;
      background: rgba(249,226,175,0.10);
      border-radius: 4px;
      font-size: 0.85rem;
      color: #f9e2af;
    }
    .ard__warn code {
      font-size: 0.78rem;
      background: rgba(0,0,0,0.30);
      padding: 1px 4px;
      border-radius: 3px;
    }
    .ard__warn--strong { border-color: rgba(243,139,168,0.40); background: rgba(243,139,168,0.10); color: #f38ba8; }

    .ard__meta {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 4px 12px;
      margin: 0 0 14px;
      font-size: 0.82rem;
    }
    .ard__meta > div { display: contents; }
    .ard__meta dt { color: rgba(255,255,255,0.55); }
    .ard__meta dd { margin: 0; color: #cdd6f4; }
    .ard__meta dd code { font-size: 0.78rem; color: #c4b5fd; }

    .ard__sub {
      font-size: 0.78rem;
      color: rgba(255,255,255,0.65);
      margin: 18px 0 6px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .ard__findings, .ard__followups, .ard__refs { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 6px; }
    .ard__finding { display: grid; grid-template-columns: 70px 160px 1fr; gap: 8px; padding: 6px 8px; background: rgba(255,255,255,0.03); border-left: 2px solid rgba(255,255,255,0.10); border-radius: 0 4px 4px 0; font-size: 0.82rem; }
    .ard__finding--info { border-left-color: rgba(148,163,184,0.45); }
    .ard__finding--warn { border-left-color: #f9e2af; }
    .ard__finding--high { border-left-color: #fab387; }
    .ard__finding-sev { color: rgba(255,255,255,0.70); font-weight: 600; text-transform: uppercase; font-size: 0.70rem; }
    .ard__finding-topic { color: #89b4fa; font-family: ui-monospace, monospace; }
    .ard__finding-msg { color: #cdd6f4; }

    .ard__followups li { padding: 8px 10px; background: rgba(255,255,255,0.03); border-radius: 4px; }
    .ard__followups li strong { color: #cdd6f4; font-size: 0.9rem; }
    .ard__followup-prio { margin-left: 6px; padding: 1px 6px; border-radius: 3px; background: rgba(137,180,250,0.15); color: #89b4fa; font-size: 0.70rem; text-transform: uppercase; }
    .ard__followups li p { margin: 4px 0 4px; color: rgba(255,255,255,0.75); font-size: 0.82rem; }
    .ard__followup-job { color: rgba(255,255,255,0.55); font-size: 0.75rem; }
    .ard__followup-job code { color: #c4b5fd; }

    .ard__refs li {
      display: flex;
      gap: 8px;
      align-items: baseline;
      padding: 4px 8px;
      background: rgba(255,255,255,0.03);
      border-radius: 4px;
      font-size: 0.80rem;
    }
    .ard__ref-kind {
      color: #94e2d5;
      text-transform: uppercase;
      font-size: 0.70rem;
      letter-spacing: 0.04em;
      min-width: 90px;
    }
    .ard__ref-id { font-family: ui-monospace, monospace; color: #cdd6f4; word-break: break-all; }
    .ard__ref-label { color: rgba(255,255,255,0.55); margin-left: auto; }

    .ard__markdown, .ard__json {
      margin: 0;
      padding: 10px 12px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 5px;
      font-size: 0.80rem;
      color: #cdd6f4;
      max-height: 360px;
      overflow: auto;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .ard__json { font-family: ui-monospace, monospace; }
    .ard__empty { color: rgba(255,255,255,0.55); font-style: italic; font-size: 0.82rem; margin: 4px 0 0; }

    .ard__actions { display: flex; gap: 6px; margin-top: 16px; flex-wrap: wrap; }
    .ard__btn {
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12);
      border-radius: 5px;
      padding: 4px 10px;
      font-size: 0.82rem;
      cursor: pointer;
    }
    .ard__btn:hover { background: rgba(255,255,255,0.10); }
  `]
})
export class AnalysisReportDrilldownComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly reportId = input.required<string>();
  readonly close = output<void>();

  private readonly svc = inject(AnalysisReportService);

  readonly report = signal<AnalysisReport | null>(null);
  readonly markdown = signal<string | null>(null);
  readonly loading = signal<boolean>(false);

  ngOnInit(): void {
    this.fetch();
  }

  fetch(): void {
    const project = this.projectName();
    const id = this.reportId();
    if (!project || !id) return;
    this.loading.set(true);
    this.svc.get(project, id).subscribe({
      next: (resp) => {
        this.report.set(resp.report);
        this.markdown.set(resp.markdown);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); },
    });
  }

  jsonView(): string {
    const r = this.report();
    if (!r) return '';
    try { return JSON.stringify(r, null, 2); } catch { return String(r); }
  }

  scopeLabel(): string {
    const r = this.report();
    if (!r) return '';
    const k = r.scope?.kind ?? 'Project';
    if (k === 'Task' && r.scope?.jobId) return `task / ${r.scope.jobId}`;
    if (k === 'Run' && r.scope?.jobId) return `run / ${r.scope.jobId}#${r.scope.runIndex ?? '?'}`;
    if (k === 'Workspace') return 'workspace';
    if (k === 'TimeWindow') return 'time-window';
    return `project / ${r.scope?.project ?? ''}`;
  }

  producerLabel(): string {
    const r = this.report();
    if (!r) return '';
    const p = r.producer;
    const parts: string[] = [p?.kind ?? 'Manual'];
    if (p?.agent) parts.push(p.agent);
    if (p?.participantId) parts.push(`@${p.participantId}`);
    return parts.join(' · ');
  }

  formatTime(iso: string | null | undefined): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  /** Helper used by reference renderers in case future overlays want to copy the id. */
  refId(ref: AnalysisReportReference): string { return ref.ref; }
}
