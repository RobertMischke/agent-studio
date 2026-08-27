import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { LocalCliRepairEvent } from '../../../cli';

@Component({
  selector: 'app-cli-repair-note',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './cli-repair-note.html',
  styleUrl: './cli-repair-note.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliRepairNoteComponent {
  readonly repair = input.required<LocalCliRepairEvent>();
  readonly label = computed(() => {
    const repair = this.repair();
    const cli = repair.cliType === 'claude' ? 'Claude CLI' : 'Codex CLI';
    return repair.succeeded ? `${cli} repaired` : `${cli} repair failed`;
  });
}
