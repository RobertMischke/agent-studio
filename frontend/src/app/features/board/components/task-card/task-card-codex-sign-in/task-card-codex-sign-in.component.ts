import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import {
  CodexSignInDialogComponent,
  ProviderAuthStatusService,
  type ProviderAuthWaitReason,
} from '../../../../remote-hosts';

@Component({
  selector: 'app-task-card-codex-sign-in',
  standalone: true,
  imports: [CodexSignInDialogComponent],
  templateUrl: './task-card-codex-sign-in.component.html',
  styleUrl: './task-card-codex-sign-in.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskCardCodexSignInComponent {
  readonly wait = input.required<ProviderAuthWaitReason>();
  readonly opened = signal(false);
  readonly runnerId = computed(() => this.wait().runnerId ?? '');
  readonly hostName = computed(() => this.wait().hostNames[0] ?? this.runnerId());
  private readonly providerAuth = inject(ProviderAuthStatusService);

  open(event: Event): void {
    event.stopPropagation();
    this.opened.set(true);
  }

  complete(): void {
    this.opened.set(false);
    this.providerAuth.refresh();
  }
}
