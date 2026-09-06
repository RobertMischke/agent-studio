import { Injectable, inject, signal } from '@angular/core';
import { TaskService } from '../../../services/task.service';
import type { ModelMigrationCatalogView, ModelMigrationProposal } from '../models/cli.model';

@Injectable({ providedIn: 'root' })
export class ModelMigrationStore {
  private readonly tasks = inject(TaskService);
  private readonly state = signal<ModelMigrationCatalogView | null>(null);
  private loading = false;

  readonly catalog = this.state.asReadonly();

  ensureLoaded(): void {
    if (this.state() || this.loading) return;
    this.reload();
  }

  reload(): void {
    if (this.loading) return;
    this.loading = true;
    this.tasks.getModelMigrations().subscribe({
      next: value => { this.state.set(value); this.loading = false; },
      error: () => { this.loading = false; },
    });
  }

  proposalFor(model: string | null | undefined): ModelMigrationProposal | null {
    if (!model) return null;
    const normalized = model.toLowerCase().replaceAll('.', '-');
    const proposals = this.state()?.migrations ?? [];
    const direct = proposals.find(item => item.from.toLowerCase().replaceAll('.', '-') === normalized);
    return direct ?? null;
  }

  proposalForExplicitPin(model: string | null | undefined, modelExplicit: boolean | undefined): ModelMigrationProposal | null {
    return modelExplicit === false ? null : this.proposalFor(model);
  }
}
