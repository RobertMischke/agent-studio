import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ProviderAuthWaitReason } from '../../models/provider-auth.model';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';
import { CodexSignInTriggerDirective } from './codex-sign-in-trigger.directive';

@Component({
  standalone: true,
  imports: [CodexSignInTriggerDirective],
  templateUrl: './codex-sign-in-trigger.directive.spec.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TriggerHostComponent {
  readonly reason: ProviderAuthWaitReason = {
    provider: 'codex',
    hostId: 'host-berlin',
    hostName: 'runner-berlin',
    aliases: ['agent-runner-01', 'host-berlin'],
    label: 'Waiting for Codex sign-in on runner-berlin',
    tooltip: 'Not logged in',
    hostNames: ['runner-berlin'],
  };
}

describe('CodexSignInTriggerDirective', () => {
  it('opens the shared sign-in dialog target', () => {
    const fixture = TestBed.createComponent(TriggerHostComponent);
    fixture.detectChanges();

    fixture.nativeElement.querySelector('button').click();

    expect(TestBed.inject(CodexSignInDialogService).active()).toEqual({
      hostId: 'host-berlin',
      hostName: 'runner-berlin',
      sshTarget: 'runner-berlin',
      aliases: ['agent-runner-01', 'host-berlin'],
    });
  });
});
