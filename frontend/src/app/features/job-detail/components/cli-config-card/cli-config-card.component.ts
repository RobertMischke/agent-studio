import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { CliSettings } from '../../../../models/job.model';

/**
 * Inline CLI configuration card surfaced when the active CLI is
 * unreachable or the user clicks "configure CLI" from an error
 * dialog. Exposes path test + save and GitHub-token save. All API
 * calls and state mutations are owned by the parent — this component
 * only renders + emits intent.
 */
@Component({
  selector: 'app-cli-config-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cli-config-card.component.html'
})
export class CliConfigCardComponent {
  readonly status = input<CliSettings | null>(null);
  readonly testResult = input<CliSettings | null>(null);
  readonly pathDraft = input<string>('');
  readonly tokenDraft = input<string>('');
  readonly testing = input(false);
  readonly tokenSaving = input(false);

  readonly closeRequest = output<void>();
  readonly pathDraftChange = output<string>();
  readonly tokenDraftChange = output<string>();
  readonly testPath = output<void>();
  readonly savePath = output<void>();
  readonly saveToken = output<void>();

  // Local-only: whether the token input is in clear-text mode.
  readonly showToken = signal(false);
}
