import { ChangeDetectionStrategy, Component, computed, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatComponent } from 'coding-agent-chat/composer';
import type { ChatMessage, ChatSubmitEvent } from 'coding-agent-chat/core';

export interface ProvisionedHostDraft {
  name: string;
  address: string;
}

interface WizardStep {
  label: string;
  eyebrow: string;
  title: string;
  description: string;
}

const STEPS: readonly WizardStep[] = [
  { label: 'Connect', eyebrow: 'Step 1 of 4', title: 'Connect to the host', description: 'Name the runner and verify its SSH target before provisioning starts.' },
  { label: 'Provision', eyebrow: 'Step 2 of 4', title: 'Provision the runner', description: 'Install the supported runtime, agent CLIs, browser dependencies, and runner binary.' },
  { label: 'CLI auth', eyebrow: 'Step 3 of 4', title: 'Authenticate agent CLIs', description: 'Log in on this host so every credential has an independent refresh-token lineage.' },
  { label: 'Smoke', eyebrow: 'Step 4 of 4', title: 'Run the smoke check', description: 'Confirm connectivity, registration, daemon startup, and one clean task handoff.' },
];

@Component({
  selector: 'app-add-host-wizard',
  standalone: true,
  imports: [FormsModule, ChatComponent],
  templateUrl: './add-host-wizard.html',
  styleUrl: './add-host-wizard.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AddHostWizardComponent {
  readonly cancelled = output<void>();
  readonly completed = output<ProvisionedHostDraft>();
  readonly step = signal(0);
  readonly name = signal('agent-runner-02');
  readonly address = signal('ssh://runner@host.example.com');
  readonly connectionChecked = signal(false);
  readonly provisioned = signal(false);
  readonly claudeAuthed = signal(false);
  readonly codexAuthed = signal(false);
  readonly smokePassed = signal(false);
  readonly chatPending = signal(false);
  readonly steps = STEPS;
  readonly current = computed(() => STEPS[this.step()]);
  readonly canContinue = computed(() => {
    switch (this.step()) {
      case 0: return !!this.name().trim() && !!this.address().trim() && this.connectionChecked();
      case 1: return this.provisioned();
      case 2: return this.claudeAuthed() && this.codexAuthed();
      default: return this.smokePassed();
    }
  });
  readonly messages = signal<ChatMessage[]>([this.message(
    'orchestrator',
    'I will stay with you through setup. Start with an Ubuntu LTS host that accepts SSH key authentication. I can explain any check before you continue.',
  )]);

  next(): void {
    if (!this.canContinue()) return;
    if (this.step() === STEPS.length - 1) {
      this.completed.emit({ name: this.name(), address: this.address() });
      return;
    }
    this.step.update((value) => value + 1);
    const hints = [
      'Connection verified. Provision the host packages and publish the runner to `/opt/agent-runner`.',
      'Provisioning is ready. Authenticate Claude and Codex on this host; do not copy credentials from the operator machine.',
      'Both CLIs are authenticated. The final smoke check should prove `/healthz`, client registration, daemon startup, and task handoff.',
    ];
    this.messages.update((items) => [...items, this.message('orchestrator', hints[this.step() - 1])]);
  }

  back(): void { this.step.update((value) => Math.max(0, value - 1)); }

  onSubmit(event: ChatSubmitEvent): void {
    const text = event.text.trim();
    if (!text) return;
    this.messages.update((items) => [...items, this.message('user', text)]);
    this.chatPending.set(true);
    const reply = this.stepReply();
    setTimeout(() => {
      this.messages.update((items) => [...items, this.message('orchestrator', reply)]);
      this.chatPending.set(false);
    }, 250);
  }

  private stepReply(): string {
    switch (this.step()) {
      case 0: return 'Use an SSH URI for the runner account. The connection check should confirm key authentication, Ubuntu LTS, and sudo access.';
      case 1: return 'Install git, curl, build-essential, .NET 10, Node 22, Claude, Codex, and Playwright Chromium. Then publish `runner/AgentRunner.csproj` to `/opt/agent-runner`.';
      case 2: return 'Run `claude` or `claude setup-token`, then `codex login`, directly on the host. Verify each CLI with its version command and a small non-interactive prompt.';
      default: return 'Run `agent-runner --health-check`, start the systemd service, and send one ready task through the remote runner. A clean external completion confirms the handoff.';
    }
  }

  private message(role: 'user' | 'orchestrator', text: string): ChatMessage {
    return { id: `${role}-${Date.now()}-${Math.random()}`, role, text, timestamp: new Date().toISOString() };
  }
}
