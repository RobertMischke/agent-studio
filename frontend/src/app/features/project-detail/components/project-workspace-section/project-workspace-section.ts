import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { JobService } from '../../../../services/job.service';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import type { JobInfo, WatchPathEntry } from '../../../../models/job.model';
import { TooltipDirective } from '../../../../components/tooltip';

/**
 * Per-project settings row: a workspace (watch path) dropdown plus a Save
 * button that reassigns every job in the current project to the selected
 * watch path. Save mirrors the per-job change-project endpoint, which is
 * the same server-side path the cross-project drag-and-drop would use.
 */
@Component({
  selector: 'app-project-workspace-section',
  standalone: true,
  imports: [FormsModule, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-workspace-section.html',
  styleUrl: './project-workspace-section.scss',
})
export class ProjectWorkspaceSectionComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly currentWatchPath = input.required<string>();

  private readonly jobService = inject(JobService);
  private readonly confirmDialog = inject(ConfirmDialogService);

  readonly watchPaths = signal<readonly WatchPathEntry[]>([]);
  readonly draft = signal<string>('');
  readonly saving = signal(false);
  readonly errorMsg = signal<string | null>(null);

  readonly canSave = computed(() => {
    const d = this.draft();
    return d !== '' && d !== this.currentWatchPath() && !this.saving();
  });

  readonly jobsInProject = computed<readonly JobInfo[]>(() => {
    const grouped = this.jobService.grouped();
    if (!grouped) return [];
    const proj = this.projectName();
    const lanes: readonly (readonly JobInfo[] | undefined)[] = [
      grouped.backlog, grouped.preparation, grouped.orchestratorPrep,
      grouped.needsHumanReview, grouped.ready, grouped.progress,
      grouped.failedPickup, grouped.review, grouped.autoReview,
      grouped.humanReview, grouped.completed, grouped.archive,
    ];
    const seen = new Set<string>();
    const out: JobInfo[] = [];
    for (const lane of lanes) {
      if (!lane) continue;
      for (const j of lane) {
        if (j.projectName !== proj) continue;
        const key = `${j.watchPath}::${j.id}`;
        if (seen.has(key)) continue;
        seen.add(key);
        out.push(j);
      }
    }
    return out;
  });

  // Sync the draft with the current watch path whenever it changes (the
  // parent's `paths().path` computed loads async via /api/projects/{name}/snapshot).
  // Only overwrites when the draft hasn't been edited and a save isn't running.
  private readonly syncDraftFx = effect(() => {
    const current = this.currentWatchPath();
    if (this.saving()) return;
    if (this.draft() === '' || this.draft() === this.lastSyncedCurrent) {
      this.draft.set(current);
      this.lastSyncedCurrent = current;
    } else {
      this.lastSyncedCurrent = current;
    }
  });

  private lastSyncedCurrent = '';

  ngOnInit(): void {
    this.jobService.getWatchPaths().subscribe({
      next: (entries) => this.watchPaths.set(entries ?? []),
      error: () => { /* leave list empty; dropdown disables */ },
    });
  }

  async onSave(): Promise<void> {
    if (!this.canSave()) return;
    const target = this.draft();
    const targetEntry = this.watchPaths().find(w => w.path === target);
    if (!targetEntry) {
      this.errorMsg.set('Selected workspace was not found.');
      return;
    }
    const jobs = this.jobsInProject();
    if (jobs.length === 0) {
      this.errorMsg.set('No jobs in this project to move.');
      return;
    }

    const ok = await this.confirmDialog.confirm({
      title: 'Move project to another workspace?',
      message:
        `This moves all ${jobs.length} job${jobs.length === 1 ? '' : 's'} from "${this.projectName()}" to "${targetEntry.name}". ` +
        'Folders are copied to the target workspace and the originals are deleted.',
      detail: `Target watch path: ${target}`,
      confirmLabel: 'Move project',
      kind: 'primary',
    });
    if (!ok) return;

    this.saving.set(true);
    this.errorMsg.set(null);

    const calls = jobs.map(j =>
      this.jobService.changeProject(j.id, target, j.watchPath).pipe(
        map(() => ({ ok: true as const })),
        catchError(() => of({ ok: false as const })),
      ),
    );

    forkJoin(calls).subscribe({
      next: (results) => {
        this.saving.set(false);
        const failures = results.filter(r => !r.ok).length;
        if (failures > 0) {
          this.errorMsg.set(`Failed to move ${failures} of ${results.length} job${results.length === 1 ? '' : 's'}.`);
        } else {
          this.errorMsg.set(null);
          this.draft.set('');
        }
        this.jobService.refresh(true);
      },
      error: () => {
        this.saving.set(false);
        this.errorMsg.set('Move failed; the workspace was not changed.');
      },
    });
  }
}
