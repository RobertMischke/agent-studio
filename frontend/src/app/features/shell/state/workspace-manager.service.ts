import { Injectable, inject, signal } from '@angular/core';
import { JobService } from '../../../services/job.service';

/**
 * Owns the visibility of the "+ Add workspace" modal plus the
 * "after-create / after-delete" refresh of the watch-path list. The
 * dialog component and the per-project Delete affordance both call
 * into this service so neither has to reach back into the studio
 * shell to know whether the project picker has the new entry.
 *
 * Kept as a tiny singleton: callers signal intent via `openCreate()`
 * / `closeCreate()` / `refreshAndClose()`; readers bind to the
 * `createOpen` and `knownNames` signals. The known-names signal is
 * filled lazily on `prime()` (called when the shell loads) so the
 * client-side uniqueness check inside the dialog has data to compare
 * against without a second round trip.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceManagerService {
  private readonly jobService = inject(JobService);

  readonly createOpen = signal(false);
  readonly knownNames = signal<readonly string[]>([]);

  /** Loads the current watch-path list into `knownNames`. Safe to call
   *  repeatedly; failures are silent so a transient backend hiccup
   *  doesn't block the dialog from opening. */
  prime(): void {
    this.jobService.getWatchPaths().subscribe({
      next: (entries) => {
        this.knownNames.set((entries ?? []).map(e => e.name));
      },
      error: () => { /* leave previous snapshot */ },
    });
  }

  openCreate(): void {
    this.prime();
    this.createOpen.set(true);
  }

  closeCreate(): void {
    this.createOpen.set(false);
  }

  /** Called by the dialog after a successful create. Re-pulls the
   *  watch-path list so the project picker lights up the new entry,
   *  then closes the dialog. The watch-path refresh also drives
   *  any subscribers in the shell that derive from the same list. */
  refreshAndClose(): void {
    this.prime();
    this.jobService.refresh(true);
    this.createOpen.set(false);
  }

  /** Mirrors `refreshAndClose` for the delete path: refresh the
   *  known list so a subsequent create dialog reflects the removal. */
  refreshAfterDelete(): void {
    this.prime();
    this.jobService.refresh(true);
  }
}
