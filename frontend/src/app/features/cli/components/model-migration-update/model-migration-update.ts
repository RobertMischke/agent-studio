import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { ModelMigrationStore } from '../../services/model-migration.store';
import { modelMigrationDiffTooltip, type ModelMigrationProposal } from '../../models/cli.model';

@Component({
  selector: 'app-model-migration-update',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './model-migration-update.html',
  styleUrl: './model-migration-update.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModelMigrationUpdateComponent implements OnInit {
  readonly proposal = input<ModelMigrationProposal | null>(null);
  readonly model = input<string | null>();
  readonly modelExplicit = input<boolean>();
  readonly taskId = input<string | null>();
  readonly watchPath = input<string | null>();
  readonly disabled = input(false);
  readonly testId = input('model-migration-update');
  readonly apply = output<string>();
  private readonly migrations = inject(ModelMigrationStore);
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  readonly update = computed(() => this.proposal()
    ?? this.migrations.proposalForExplicitPin(this.model(), this.modelExplicit()));
  readonly tooltip = computed(() => this.update() ? modelMigrationDiffTooltip(this.update()!) : '');

  ngOnInit(): void {
    if (!this.proposal()) this.migrations.ensureLoaded();
  }

  select(event: Event): void {
    event.stopPropagation();
    const update = this.update();
    if (!update || this.disabled()) return;
    const taskId = this.taskId();
    if (!taskId) {
      this.apply.emit(update.to);
      return;
    }
    this.tasks.setJobModel(taskId, update.to, this.watchPath() ?? undefined).subscribe({
      next: () => this.notifications.success(`Model updated to ${update.to}.`),
      error: () => this.notifications.error('Could not update the task model.'),
    });
  }
}
