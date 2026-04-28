import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CliModelInfo, CliType, CLI_TYPES, WatchPathEntry } from '../../../models/job.model';
import { cliTypeIcon as fmtCliTypeIcon, cliTypeLabel as fmtCliTypeLabel, formatMultiplier as fmtMultiplier } from '../../../services/format.util';

/**
 * "Create task" dialog. The parent owns all draft signals and the
 * model catalog; this component only renders the form and emits
 * intent (cancel / submit / cliType change). Two-way bindings via
 * `model()` keep title / watchPath / model / prompt in sync.
 */
@Component({
  selector: 'app-create-job-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './create-job-dialog.component.html'
})
export class CreateJobDialogComponent {
  readonly title = input<string>('');
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly availableModels = input<CliModelInfo[]>([]);
  readonly cliTypeDraft = input.required<CliType>();

  readonly newTitle = model<string>('');
  readonly newWatchPath = model<string>('');
  readonly newModel = model<string>('');
  readonly newPrompt = model<string>('');

  readonly cliTypeChange = output<CliType>();
  readonly cancel = output<void>();
  readonly submit = output<void>();

  readonly cliTypes = CLI_TYPES;
  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }
  cliTypeIcon(t: CliType): string { return fmtCliTypeIcon(t); }
  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }
}
