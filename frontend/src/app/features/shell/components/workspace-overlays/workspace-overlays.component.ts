import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { WorkspaceOverlaysService } from '../../state/workspace-overlays.service';
import type { WorkspaceSettingsSection } from '../../state/workspace-overlays.service';
import { WorkspaceScreenshotsComponent } from '../../../screenshots';
import { TokenUsageSectionComponent } from '../../../tokens';
import { CliAdminPanelComponent, CliWorkingMemoryPanelComponent } from '../../../cli';
import { RemoteHostsPanelComponent } from '../../../remote-hosts';
import { PromptAdminPanelComponent } from '../../../orchestrator';
// Direct path (not the studio-shell barrel) so we don't pull StudioShellComponent
// and re-form the shell <-> studio-shell import cycle (AGT-2035).
import { AppearanceSettingsComponent } from '../../../studio-shell/components/appearance-settings/appearance-settings.component';
import { UpdatesSettingsComponent } from '../../../update';
import { WorkspaceManagementComponent } from '../workspace-management/workspace-management.component';
import type { TaskScreenshot } from '../../../../features/screenshots';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import { TaskService } from '../../../../services/task.service';
import type { ProjectSourceDescriptor } from '../../../../models/task.model';

import { TooltipDirective } from 'coding-agent-chat/shared';

/** Rail-grouping bucket for the consolidated settings sections. */
type SettingsRailGroup = 'general' | 'global' | 'workspace';

interface SettingsRailItem {
  key: WorkspaceSettingsSection;
  label: string;
  description: string;
  icon: string;
  group: SettingsRailGroup;
}

/**
 * The one consolidated Workspace-settings view (AGT-2035). It replaces the
 * former split between the studio-shell sidebar "Settings" panel and the
 * scattered workspace overlays: one rail + panel with a clean Global-vs-
 * Workspace grouping.
 *
 * Global group (per-user / app-wide): Appearance (Theme + Activity bar),
 * Updates, Workspaces (registry management). Workspace group (defaults applied
 * across the workspace's projects): Usage caps, Working memory, System prompts,
 * Token usage, Visual evidence.
 *
 * Each content section re-uses a stable outer test id
 * (`cli-admin-overlay`, `prompt-admin-overlay`, `workspace-tokens-overlay`,
 * `workspace-screenshots-overlay`, plus the new appearance/updates/
 * workspaces/working-memory ids) so deep-links and specs keep resolving.
 */
@Component({
  selector: 'app-workspace-overlays',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    TokenUsageSectionComponent,
    WorkspaceScreenshotsComponent,
    CliAdminPanelComponent,
    CliWorkingMemoryPanelComponent,
    RemoteHostsPanelComponent,
    PromptAdminPanelComponent,
    AppearanceSettingsComponent,
    UpdatesSettingsComponent,
    WorkspaceManagementComponent,
    TooltipDirective,
    OverlayPortalDirective,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-overlays.component.html',
  styleUrl: './workspace-overlays.component.scss',
})
export class WorkspaceOverlaysComponent {
  readonly overlays = inject(WorkspaceOverlaysService);
  /** Render inside the Studio editor tab instead of as a modal dialog. */
  readonly inline = input(false);
  readonly openTask = output<TaskScreenshot>();
  /** Project name whose Settings rail the shell should open (bubbled up from
   *  the Token-usage section's per-project usage rows). */
  readonly openProjectSettings = output<string>();
  /** Task the shell should open in its detail panel (bubbled up from the
   *  usage-caps CLI-sessions list). */
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  private readonly modalStack = inject(ModalStackService);
  private readonly tasks = inject(TaskService);
  readonly projectSources = signal<readonly ProjectSourceDescriptor[]>([]);
  private readonly destroyRef = inject(DestroyRef);
  private modalDisposer: (() => void) | null = null;

  /** Rail order: overview first, then the Global group, then the Workspace group. */
  readonly railItems: readonly SettingsRailItem[] = [
    { key: 'overview', label: 'Overview', description: 'Everything in one place, split into Global and Workspace.', icon: '\u{1F3E0}', group: 'general' },
    { key: 'appearance', label: 'Appearance', description: 'Theme and activity-bar side. Applies to you everywhere.', icon: '\u{1F3A8}', group: 'global' },
    { key: 'updates', label: 'Updates', description: 'Keep this instance in sync with stable.', icon: '\u{1F504}', group: 'global' },
    { key: 'workspaces', label: 'Workspaces', description: 'Manage every workspace and its projects.', icon: '\u{1F5C2}', group: 'global' },
    { key: 'remote-hosts', label: 'Remote hosts', description: 'Execution locations: heartbeat, vitals, quota, and Re-Probe / Drain / Retire.', icon: '\u{1F4E1}', group: 'global' },
    { key: 'project-sources', label: 'Project sources', description: 'Available origins for newly onboarded projects.', icon: '\u{1F4C1}', group: 'global' },
    { key: 'caps', label: 'Usage caps', description: 'Per-CLI quota caps and runner rules.', icon: '⚙', group: 'workspace' },
    { key: 'working-memory', label: 'Working memory', description: 'Per-CLI memory and session state. Auth stays protected.', icon: '\u{1F9E0}', group: 'workspace' },
    { key: 'prompts', label: 'System prompts', description: 'Application-wide runtime prompt defaults and overrides.', icon: 'T', group: 'workspace' },
    { key: 'tokens', label: 'Token usage', description: 'The single usage area: token spend across every project.', icon: '\u{1F4CA}', group: 'workspace' },
    { key: 'screenshots', label: 'Visual evidence', description: 'Screenshots captured by tasks across all projects.', icon: '\u{1F441}', group: 'workspace' },
  ];

  /** Human labels for the rail group headers. */
  readonly groupLabels: Record<SettingsRailGroup, string> = {
    general: 'General',
    global: 'Global',
    workspace: 'Workspace',
  };

  /** The sections surfaced as cards on the overview landing. */
  get contentItems(): readonly SettingsRailItem[] {
    return this.railItems.filter(i => i.key !== 'overview');
  }

  /** True when `item` is the first of its group, so the rail draws a header. */
  isGroupStart(index: number): boolean {
    if (index === 0) return true;
    return this.railItems[index].group !== this.railItems[index - 1].group;
  }

  constructor() {
    this.tasks.getProjectSources().subscribe({ next: sources => this.projectSources.set(sources), error: () => this.projectSources.set([]) });
    // One modal-stack registration tracks the whole view so Escape and
    // backdrop ordering behave like the other studio modals.
    effect(() => {
      const open = this.overlays.settingsOpen();
      if (this.inline()) {
        this.modalDisposer?.();
        this.modalDisposer = null;
        return;
      }
      if (open && !this.modalDisposer) {
        this.modalDisposer = this.modalStack.push('workspace-settings', () => this.overlays.close());
      } else if (!open && this.modalDisposer) {
        this.modalDisposer();
        this.modalDisposer = null;
      }
    });
    this.destroyRef.onDestroy(() => {
      this.modalDisposer?.();
      this.modalDisposer = null;
    });
  }

  /** Outer test id of the active panel, preserved per section so legacy
   *  deep-link specs keep resolving against the same hook. */
  panelTestid(): string {
    switch (this.overlays.section()) {
      case 'caps': return 'cli-admin-overlay';
      case 'prompts': return 'prompt-admin-overlay';
      case 'tokens': return 'workspace-tokens-overlay';
      case 'screenshots': return 'workspace-screenshots-overlay';
      case 'appearance': return 'workspace-appearance-overlay';
      case 'updates': return 'workspace-updates-overlay';
      case 'workspaces': return 'workspace-management-overlay';
      case 'remote-hosts': return 'workspace-remote-hosts-overlay';
      case 'project-sources': return 'workspace-project-sources-overlay';
      case 'working-memory': return 'workspace-working-memory-overlay';
      case 'overview': return 'workspace-settings-overview-panel';
    }
  }

  onBackdrop(event: Event): void {
    if (event.target === event.currentTarget) this.overlays.close();
  }
}
