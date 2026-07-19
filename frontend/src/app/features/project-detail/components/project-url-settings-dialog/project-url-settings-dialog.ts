import { ChangeDetectionStrategy, Component, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type { ProjectUrlStartRule, RegistryProjectUrl } from '../../../../models/task.model';

export interface ProjectUrlSettingsValue {
  label: string;
  url: string;
  startRule: ProjectUrlStartRule | null;
}

/** Compact editor for the URL and start rule of the currently open embed. */
@Component({
  selector: 'app-project-url-settings-dialog',
  standalone: true,
  imports: [DialogComponent, FormsModule, PendingButtonDirective],
  templateUrl: './project-url-settings-dialog.html',
  styleUrl: './project-url-settings-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectUrlSettingsDialogComponent {
  readonly url = input.required<RegistryProjectUrl>();
  readonly saving = input(false);
  readonly error = input<string | null>(null);
  readonly saveRequest = output<ProjectUrlSettingsValue>();
  readonly closeRequest = output<void>();

  readonly address = signal('');
  readonly command = signal('');
  readonly cwd = signal('');
  readonly port = signal<number | null>(null);

  constructor() {
    effect(() => {
      const url = this.url();
      this.address.set(url.url);
      this.command.set(url.startRule?.command ?? '');
      this.cwd.set(url.startRule?.cwd ?? '');
      this.port.set(url.startRule?.port ?? null);
    });
  }

  save(): void {
    const current = this.url();
    const command = this.command().trim();
    this.saveRequest.emit({
      label: current.label,
      url: this.address().trim(),
      startRule: command ? {
        command,
        cwd: this.cwd().trim() || null,
        port: this.port(),
        source: 'manual',
      } : null,
    });
  }
}
