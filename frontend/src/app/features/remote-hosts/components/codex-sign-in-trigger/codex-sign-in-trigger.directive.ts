import { Directive, HostListener, inject, input } from '@angular/core';
import type { ProviderAuthWaitReason } from '../../models/provider-auth.model';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';

@Directive({ selector: '[appCodexSignInTrigger]', standalone: true })
export class CodexSignInTriggerDirective {
  readonly target = input.required<ProviderAuthWaitReason>({ alias: 'appCodexSignInTrigger' });
  private readonly dialog = inject(CodexSignInDialogService);

  @HostListener('click', ['$event'])
  open(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    const target = this.target();
    this.dialog.open({
      hostId: target.hostId,
      hostName: target.hostName,
      sshTarget: target.hostName,
      aliases: target.aliases,
    });
  }
}
