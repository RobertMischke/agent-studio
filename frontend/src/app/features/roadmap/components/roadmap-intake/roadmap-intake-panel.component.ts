import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobService } from '../../../../services/job.service';
import { RoadmapIntakeCandidate } from '../../../../models/job.model';

/**
 * Two-step "Send to roadmap" surface. The user pastes a long, often
 * multi-language dump; the splitter returns candidate tasks; the user
 * edits each one in place and confirms. On confirm, one job folder is
 * written per candidate into `1-preparation` (never `2-ready`) so the
 * board still gets a human review pass.
 *
 * Self-contained on purpose: the host (orchestrator side sheet) wires
 * this component into a tab and feeds it the active project's
 * watchPath. All draft / preview state lives here.
 */
@Component({
  selector: 'app-roadmap-intake-panel',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="intake" data-testid="roadmap-intake-panel">
      @if (phase() === 'compose') {
        <header class="intake__intro">
          <h3 class="intake__title">Send to roadmap</h3>
          <p class="intake__hint">
            Paste a long brain dump. The splitter turns it into reviewable
            task candidates. Nothing is queued until you confirm; candidates
            land in <strong>1-preparation</strong>.
          </p>
        </header>

        @if (errorMsg()) {
          <div class="intake__error" data-testid="roadmap-intake-error">{{ errorMsg() }}</div>
        }

        <textarea class="intake__textarea"
                  data-testid="roadmap-intake-input"
                  rows="10"
                  [disabled]="splitting()"
                  [placeholder]="'Drop a roadmap-style message here. German is fine; the splitter rewrites stored prompts to English.'"
                  [(ngModel)]="draftText"
                  [ngModelOptions]="{ standalone: true }"></textarea>

        <div class="intake__actions">
          <button type="button"
                  class="intake__btn intake__btn--primary"
                  data-testid="roadmap-intake-split"
                  [disabled]="splitting() || !canSplit()"
                  (click)="split()">
            {{ splitting() ? 'Splitting…' : 'Split into candidates' }}
          </button>
        </div>
      } @else if (phase() === 'preview') {
        <header class="intake__intro">
          <h3 class="intake__title">Review candidates</h3>
          <p class="intake__hint">
            Edit any field in place. Uncheck a row to skip it. On confirm,
            checked rows are written as job folders in
            <strong>1-preparation</strong> on
            <strong>{{ projectName() || activeWatchPath() }}</strong>.
          </p>
          @if (notes()) {
            <p class="intake__notes" data-testid="roadmap-intake-notes">Note: {{ notes() }}</p>
          }
        </header>

        @if (errorMsg()) {
          <div class="intake__error" data-testid="roadmap-intake-error">{{ errorMsg() }}</div>
        }

        @if (candidates().length === 0) {
          <div class="intake__empty" data-testid="roadmap-intake-empty">
            The splitter returned no candidates. The dump may have been a question
            or pure context.
          </div>
        } @else {
          <ul class="intake__list" data-testid="roadmap-intake-list">
            @for (c of candidates(); track c.localId; let i = $index) {
              <li class="intake__item"
                  [class.intake__item--skipped]="!c.included"
                  [attr.data-testid]="'roadmap-intake-item-' + i">
                <header class="intake__item-head">
                  <label class="intake__include">
                    <input type="checkbox"
                           [checked]="c.included"
                           [attr.data-testid]="'roadmap-intake-include-' + i"
                           (change)="toggleInclude(c.localId, $event)" />
                    Include
                  </label>
                  <select class="intake__kind"
                          [value]="c.kind"
                          [attr.data-testid]="'roadmap-intake-kind-' + i"
                          (change)="updateField(c.localId, 'kind', $event)">
                    <option value="feature">feature</option>
                    <option value="bug">bug</option>
                    <option value="adr">adr</option>
                    <option value="chore">chore</option>
                    <option value="research">research</option>
                  </select>
                  <select class="intake__cli"
                          [value]="c.suggestedCliType"
                          [attr.data-testid]="'roadmap-intake-cli-' + i"
                          (change)="updateField(c.localId, 'suggestedCliType', $event)">
                    <option value="claude">claude</option>
                    <option value="codex">codex</option>
                    <option value="copilot">copilot</option>
                    <option value="gemini">gemini</option>
                  </select>
                </header>
                <input class="intake__title-input"
                       [value]="c.title"
                       [attr.data-testid]="'roadmap-intake-title-' + i"
                       (input)="updateField(c.localId, 'title', $event)" />
                <textarea class="intake__body-input"
                          rows="6"
                          [value]="c.promptBody"
                          [attr.data-testid]="'roadmap-intake-body-' + i"
                          (input)="updateField(c.localId, 'promptBody', $event)"></textarea>
                @if (c.rationale) {
                  <p class="intake__rationale">{{ c.rationale }}</p>
                }
              </li>
            }
          </ul>
        }

        <div class="intake__actions">
          <button type="button"
                  class="intake__btn"
                  data-testid="roadmap-intake-back"
                  [disabled]="confirming()"
                  (click)="back()">← Edit dump</button>
          <button type="button"
                  class="intake__btn intake__btn--primary"
                  data-testid="roadmap-intake-confirm"
                  [disabled]="confirming() || includedCount() === 0"
                  (click)="confirm()">
            {{ confirming() ? 'Creating…' : 'Create ' + includedCount() + ' draft' + (includedCount() === 1 ? '' : 's') }}
          </button>
        </div>
      } @else if (phase() === 'done') {
        <header class="intake__intro">
          <h3 class="intake__title">Drafts created</h3>
        </header>
        <ul class="intake__done-list" data-testid="roadmap-intake-done-list">
          @for (j of createdJobs(); track j.jobId) {
            <li>✓ <strong>{{ j.title }}</strong> <span class="intake__id">({{ j.jobId }})</span></li>
          }
          @for (s of skipped(); track s) {
            <li class="intake__skipped-line">⚠ Skipped: {{ s }}</li>
          }
        </ul>
        <p class="intake__hint">
          Find them in <strong>1-preparation</strong> on the board. Move them to
          <strong>2-ready</strong> when you have reviewed each one.
        </p>
        <div class="intake__actions">
          <button type="button"
                  class="intake__btn"
                  data-testid="roadmap-intake-restart"
                  (click)="reset()">New dump</button>
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; flex: 1; min-height: 0; }
    .intake {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
      gap: 10px;
      color: #e2e8f0;
      font-size: 13px;
      overflow-y: auto;
      padding: 4px 2px;
    }
    .intake__intro { display: flex; flex-direction: column; gap: 4px; }
    .intake__title {
      margin: 0;
      font-size: 14px;
      font-weight: 700;
      color: #f5f3ff;
    }
    .intake__hint {
      margin: 0;
      color: #94a3b8;
      font-size: 12px;
      line-height: 1.5;
    }
    .intake__notes {
      margin: 4px 0 0;
      color: #fcd34d;
      font-size: 12px;
    }
    .intake__error {
      padding: 6px 10px;
      background: rgba(244,63,94,0.1);
      border: 1px solid rgba(244,63,94,0.35);
      color: #fda4af;
      border-radius: 8px;
      font-size: 12px;
    }
    .intake__textarea {
      flex: 1;
      min-height: 200px;
      background: rgba(0,0,0,0.30);
      color: #e2e8f0;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      padding: 8px 10px;
      font-family: 'Inter', system-ui, sans-serif;
      font-size: 13px;
      line-height: 1.5;
      resize: vertical;
    }
    .intake__textarea:focus { outline: none; border-color: #6366f1; }
    .intake__actions {
      display: flex;
      gap: 8px;
      justify-content: flex-end;
    }
    .intake__btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #cbd5e1;
      padding: 6px 12px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 12.5px;
    }
    .intake__btn:hover:not(:disabled) {
      background: rgba(255,255,255,0.10);
      color: #e2e8f0;
    }
    .intake__btn:disabled { opacity: 0.4; cursor: not-allowed; }
    .intake__btn--primary {
      background: rgba(99,102,241,0.85);
      border-color: rgba(165,180,252,0.85);
      color: #ffffff;
      font-weight: 600;
    }
    .intake__btn--primary:hover:not(:disabled) {
      background: rgba(99,102,241,1);
      border-color: rgba(196,181,253,1);
    }

    .intake__list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .intake__item {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 10px;
      border: 1px solid rgba(196,181,253,0.28);
      background: rgba(76,29,149,0.10);
      border-radius: 10px;
    }
    .intake__item--skipped {
      opacity: 0.45;
      border-color: rgba(148,163,184,0.18);
      background: rgba(15,23,42,0.4);
    }
    .intake__item-head {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }
    .intake__include {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      font-size: 11px;
      color: #cbd5e1;
    }
    .intake__kind, .intake__cli {
      background: rgba(0,0,0,0.30);
      color: #e2e8f0;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 6px;
      font-size: 11px;
      padding: 2px 6px;
    }
    .intake__title-input {
      background: rgba(0,0,0,0.30);
      color: #f5f3ff;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 6px;
      padding: 5px 8px;
      font-size: 13px;
      font-weight: 600;
    }
    .intake__title-input:focus { outline: none; border-color: #6366f1; }
    .intake__body-input {
      background: rgba(0,0,0,0.30);
      color: #e2e8f0;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 6px;
      padding: 6px 8px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 12px;
      line-height: 1.45;
      resize: vertical;
    }
    .intake__body-input:focus { outline: none; border-color: #6366f1; }
    .intake__rationale {
      margin: 0;
      color: #c4b5fd;
      font-size: 11.5px;
      font-style: italic;
    }

    .intake__empty {
      padding: 20px 12px;
      text-align: center;
      color: #94a3b8;
      font-style: italic;
      border: 1px dashed rgba(255,255,255,0.10);
      border-radius: 8px;
    }

    .intake__done-list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .intake__done-list li {
      padding: 6px 10px;
      background: rgba(20,184,166,0.10);
      border: 1px solid rgba(94,234,212,0.30);
      border-radius: 6px;
      color: #a7f3d0;
      font-size: 12px;
    }
    .intake__skipped-line {
      background: rgba(244,63,94,0.10) !important;
      border-color: rgba(244,63,94,0.30) !important;
      color: #fda4af !important;
    }
    .intake__id {
      color: #94a3b8;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11px;
    }
  `]
})
export class RoadmapIntakePanelComponent {
  readonly activeWatchPath = input<string | null>(null);
  readonly projectName = input<string | null>(null);

  readonly created = output<{ count: number }>();

  readonly phase = signal<'compose' | 'preview' | 'done'>('compose');
  readonly splitting = signal(false);
  readonly confirming = signal(false);
  readonly errorMsg = signal<string | null>(null);

  draftText = '';

  readonly notes = signal<string>('');
  readonly candidates = signal<EditableCandidate[]>([]);
  readonly createdJobs = signal<{ jobId: string; title: string }[]>([]);
  readonly skipped = signal<string[]>([]);

  readonly includedCount = computed(() => this.candidates().filter((c) => c.included).length);

  private readonly jobService = inject(JobService);

  canSplit(): boolean {
    return this.draftText.trim().length > 0 && !!this.activeWatchPath();
  }

  split(): void {
    const watchPath = this.activeWatchPath();
    if (!watchPath || this.splitting()) return;
    const text = this.draftText.trim();
    if (text.length === 0) return;

    this.splitting.set(true);
    this.errorMsg.set(null);
    this.jobService.splitRoadmapIntake(text, watchPath).subscribe({
      next: (resp) => {
        this.splitting.set(false);
        this.notes.set(resp?.notes ?? '');
        const list: EditableCandidate[] = (resp?.candidates ?? []).map((c, i) => ({
          ...c,
          localId: `c-${i}-${Math.random().toString(36).slice(2, 7)}`,
          included: true
        }));
        this.candidates.set(list);
        this.phase.set('preview');
      },
      error: (err) => {
        this.splitting.set(false);
        const message = err?.error?.error || err?.error?.detail || err?.message || 'Splitter failed';
        this.errorMsg.set(message);
      }
    });
  }

  back(): void {
    this.phase.set('compose');
    this.errorMsg.set(null);
  }

  toggleInclude(localId: string, event: Event): void {
    const checked = (event.target as HTMLInputElement | null)?.checked ?? true;
    this.candidates.update((list) =>
      list.map((c) => (c.localId === localId ? { ...c, included: checked } : c))
    );
  }

  updateField(localId: string, field: keyof RoadmapIntakeCandidate, event: Event): void {
    const target = event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement | null;
    if (!target) return;
    const value = target.value;
    this.candidates.update((list) =>
      list.map((c) => (c.localId === localId ? { ...c, [field]: value } : c))
    );
  }

  confirm(): void {
    const watchPath = this.activeWatchPath();
    if (!watchPath || this.confirming()) return;
    const picked = this.candidates().filter((c) => c.included);
    if (picked.length === 0) return;

    const payload: RoadmapIntakeCandidate[] = picked.map((c) => ({
      title: c.title,
      promptBody: c.promptBody,
      kind: c.kind,
      suggestedOrder: c.suggestedOrder,
      suggestedCliType: c.suggestedCliType,
      rationale: c.rationale
    }));

    this.confirming.set(true);
    this.errorMsg.set(null);
    this.jobService.confirmRoadmapIntake(watchPath, payload).subscribe({
      next: (resp) => {
        this.confirming.set(false);
        this.createdJobs.set(resp?.created ?? []);
        this.skipped.set(resp?.skipped ?? []);
        this.phase.set('done');
        this.created.emit({ count: resp?.created?.length ?? 0 });
      },
      error: (err) => {
        this.confirming.set(false);
        const message = err?.error?.error || err?.message || 'Confirm failed';
        this.errorMsg.set(message);
      }
    });
  }

  reset(): void {
    this.draftText = '';
    this.candidates.set([]);
    this.notes.set('');
    this.createdJobs.set([]);
    this.skipped.set([]);
    this.errorMsg.set(null);
    this.phase.set('compose');
  }
}

interface EditableCandidate extends RoadmapIntakeCandidate {
  localId: string;
  included: boolean;
}
