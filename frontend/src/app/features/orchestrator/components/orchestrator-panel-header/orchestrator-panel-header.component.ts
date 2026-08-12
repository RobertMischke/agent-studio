import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  StudioIconComponent,
  type StudioIconName,
} from '../../../../components/studio-icon/studio-icon.component';
import type { PageContext } from '../../../../models/page-context.model';
import type { ChatExecutionContext } from '../../models/orchestrator.model';

interface PanelContextIdentity {
  icon: StudioIconName;
  type: string;
  name: string;
}

@Component({
  selector: 'app-orchestrator-panel-header',
  standalone: true,
  imports: [StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-panel-header.component.html',
  styleUrl: './orchestrator-panel-header.component.scss',
})
export class OrchestratorPanelHeaderComponent {
  readonly project = input<string | null>(null);
  readonly taskKey = input<string | null>(null);
  readonly taskTitle = input<string | null>(null);
  readonly pageContext = input<PageContext | null>(null);
  readonly dossierKey = input<string | null>(null);
  readonly dossierTitle = input<string | null>(null);
  readonly contextKey = input<string | null>(null);
  readonly executionContext = input<ChatExecutionContext | null>(null);
  readonly contextCount = input(0);
  readonly chatsOpen = input(false);
  readonly active = input(false);
  readonly chatsToggle = output<void>();

  readonly identity = computed<PanelContextIdentity>(() => {
    const project = this.project();
    if (this.taskKey()) {
      const taskName = [this.taskKey(), this.taskTitle()].filter(Boolean).join(' · ');
      return { icon: 'code', type: 'Task', name: taskName };
    }
    if (this.dossierKey() || this.dossierTitle()) {
      return {
        icon: 'eye',
        type: 'Dossier',
        name: this.dossierKey() ?? this.dossierTitle() ?? 'Current Dossier',
      };
    }
    if (this.contextKey() === 'global')
      return { icon: 'bot', type: 'Global', name: 'All projects' };
    if (project) return { icon: 'folder', type: 'Project', name: project };
    return { icon: 'bot', type: 'Chat', name: 'No context selected' };
  });

  readonly identityTitle = computed(() => `${this.identity().type}: ${this.identity().name}`);
  readonly executionHostLabel = computed(() => {
    const context = this.executionContext();
    if (!context) return 'Runner unavailable';
    return context.executionKind === 'local' ? 'Local' : context.hostName;
  });
  readonly executionRepoLabel = computed(() => {
    const context = this.executionContext();
    if (!context) return '';
    return context.state === 'ready' && context.repoPath ? context.repoPath : 'Resolving checkout';
  });
  readonly executionRevisionLabel = computed(() => {
    const context = this.executionContext();
    if (!context) return '';
    if (context.state !== 'ready' || !context.repoPath) return context.branch ?? 'project';
    return `· ${context.branch ?? 'detached'}@${context.headSha?.slice(0, 8) ?? 'unknown'}`;
  });
  readonly executionRefLabel = computed(() => {
    const revision = this.executionRevisionLabel().replace(/^·\s*/, '');
    return [this.executionRepoLabel(), revision].filter(Boolean).join(' · ');
  });
  readonly executionTitle = computed(() => {
    const context = this.executionContext();
    if (!context) return 'Execution context unavailable';
    return [
      `Execution: ${this.executionHostLabel()}`,
      `Repository: ${context.repoPath ?? 'resolving'}`,
      `Branch: ${context.branch ?? 'unknown'}`,
      `HEAD: ${context.headSha ?? 'unknown'}`,
    ].join('\n');
  });
  readonly statusState = computed(() => {
    if (this.active()) return 'active';
    return this.executionContext()?.state === 'ready' ? 'ready' : 'resolving';
  });
}
