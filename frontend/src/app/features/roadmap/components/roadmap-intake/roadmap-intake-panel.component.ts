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
import type { RoadmapIntakeCandidate } from '../../../../features/roadmap';

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
  templateUrl: './roadmap-intake-panel.component.html',
  styleUrl: './roadmap-intake-panel.component.scss'
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
