import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CliModelInfo, CliType, CLI_TYPES, WatchPathEntry } from '../../../../models/job.model';
import { cliTypeIcon as fmtCliTypeIcon, cliTypeLabel as fmtCliTypeLabel, formatMultiplier as fmtMultiplier } from '../../../../services/format.util';

/**
 * Top toolbar of the job-detail view: project picker, CLI tab strip,
 * model dropdown, and the live elapsed-time / start / stop controls.
 *
 * Stateless / presentational: the parent owns the model + CLI selection
 * (via two-way `model` bindings), watchPaths (list), and the run-state
 * signals; this component only emits intent (start/stop, change project).
 */
@Component({
  selector: 'app-command-deck',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './command-deck.component.html',
  styleUrls: ['./command-deck.component.scss']
})
export class CommandDeckComponent {
  readonly currentWatchPath = input.required<string>();
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly cliTypeDraft = input.required<CliType>();
  readonly modelDraft = input.required<string>();
  readonly availableModels = input<CliModelInfo[]>([]);

  readonly isRunning = input(false);
  readonly canStart = input(false);
  readonly startDisabledReason = input<string | null>(null);
  readonly starting = input(false);
  readonly elapsedTime = input('');
  /** Compact mode: hides selectors and shows a "Show setup" toggle.
   *  Owned by the parent (auto-collapsed while a run is active, manually
   *  re-expandable). The component just renders what it's told. */
  readonly collapsed = input(false);

  readonly projectChange = output<string>();
  readonly cliTypeChange = output<CliType>();
  readonly modelChange = output<string>();
  readonly start = output<void>();
  readonly stop = output<void>();
  readonly toggleCollapsed = output<void>();

  readonly cliTypes = CLI_TYPES;
  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }
  cliTypeIcon(t: CliType): string { return fmtCliTypeIcon(t); }
  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }
}
