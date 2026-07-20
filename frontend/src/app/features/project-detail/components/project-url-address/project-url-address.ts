import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';

/**
 * Editable address field of the URL preview chrome.
 *
 * Renders the "Project / Label" identity plus the effective preview URL as a
 * real text input, so the operator can select and copy the URL or type a new
 * target. Enter commits the draft and emits `navigate`; Escape or blur
 * discards it. The field never persists to the registry - persistent URL
 * changes stay in the embed settings dialog.
 */
@Component({
  selector: 'app-project-url-address',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-url-address.html',
  styleUrl: './project-url-address.scss',
})
export class ProjectUrlAddressComponent {
  readonly identity = input.required<string>();
  /** Effective URL shown at rest; null while the record is still resolving. */
  readonly url = input<string | null>(null);
  readonly editable = input(true);
  /** Fired on Enter with the trimmed, non-empty address. */
  readonly navigate = output<string>();

  /** Uncommitted edit; null means the field mirrors the effective URL. */
  readonly draft = signal<string | null>(null);

  commit(element: HTMLInputElement): void {
    const target = (this.draft() ?? this.url() ?? '').trim();
    this.draft.set(null);
    element.blur();
    if (target) this.navigate.emit(target);
  }

  cancel(element: HTMLInputElement): void {
    this.draft.set(null);
    element.blur();
  }
}
