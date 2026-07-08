import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output } from '@angular/core';
import { WorkspaceOverlaysService } from '../../state/workspace-overlays.service';
import type { WorkspaceSettingsSection } from '../../state/workspace-overlays.service';
import { WorkspaceTokenTimelineComponent } from '../../../tokens';
import { WorkspaceScreenshotsComponent } from '../../../screenshots';
import { WorkspaceSummaryComponent } from '../../../summary';
import { CliAdminPanelComponent } from '../../../cli';
import { PromptAdminPanelComponent } from '../../../orchestrator';
import type { TaskScreenshot } from '../../../../features/screenshots';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';

import { TooltipDirective } from 'coding-agent-chat/shared';

interface SettingsRailItem {
  key: WorkspaceSettingsSection;
  label: string;
  description: string;
  icon: string;
}

/**
 * Global Workspace-settings home ("Dach"). One rail+panel modal that
 * consolidates the formerly scattered workspace overlays (token
 * timeline, visual-evidence reel, executive summary, CLI usage caps)
 * into addressable sections, mirroring the project-level settings
 * layout. State + URL-hash sync own by WorkspaceOverlaysService; this
 * component renders the rail, the active panel, and the overview cards.
 *
 * The active panel re-uses each section's legacy outer test id
 * (`workspace-tokens-overlay`, `workspace-screenshots-overlay`,
 * `workspace-summary-overlay`, `cli-admin-overlay`) so existing
 * deep-links and specs keep resolving against the same hooks.
 *
 * The screenshots section emits `openTask` (job picked from the reel)
 * up to the shell because navigating to a job is shell-coordinated: the
 * shell owns `selectedJob` and the URL update path.
 */
@Component({
  selector: 'app-workspace-overlays',
  standalone: true,
  imports: [NgTemplateOutlet, WorkspaceTokenTimelineComponent, WorkspaceScreenshotsComponent, WorkspaceSummaryComponent, CliAdminPanelComponent, PromptAdminPanelComponent, TooltipDirective, OverlayPortalDirective],
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
   *  the usage-caps panel's per-project usage rows). Navigation is shell-
   *  coordinated, so the shell — not this overlay — owns the route change. */
  readonly openProjectSettings = output<string>();
  /** Task the shell should open in its detail panel (bubbled up from the
   *  usage-caps panel's CLI-sessions list when a session's task-link chip is
   *  clicked). Shell-coordinated, same as `openTask`. */
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private modalDisposer: (() => void) | null = null;

  /** Rail order: landing first, then the content sections. */
  readonly railItems: readonly SettingsRailItem[] = [
    { key: 'overview', label: 'Overview', description: 'Global defaults and usage surfaces, in one place.', icon: '\u{1F3E0}' },
    { key: 'caps', label: 'Usage caps', description: 'Per-CLI quota caps and runner rules, with full usage detail.', icon: '⚙' },
    { key: 'prompts', label: 'System prompts', description: 'Application-wide runtime prompt defaults and overrides.', icon: 'T' },
    { key: 'tokens', label: 'Token usage', description: 'Orchestrator token spend across every watched project.', icon: '\u{1F4CA}' },
    { key: 'screenshots', label: 'Visual evidence', description: 'Screenshots captured by tasks across all projects.', icon: '\u{1F441}' },
    { key: 'summary', label: 'Summary', description: 'Executive summary of what happened recently.', icon: '\u{1F4D6}' },
  ];

  /** The sections surfaced as cards on the overview landing. */
  get contentItems(): readonly SettingsRailItem[] {
    return this.railItems.filter(i => i.key !== 'overview');
  }

  constructor() {
    // One modal-stack registration tracks the whole home so Escape and
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
      case 'summary': return 'workspace-summary-overlay';
      case 'overview': return 'workspace-settings-overview-panel';
    }
  }

  onBackdrop(event: Event): void {
    if (event.target === event.currentTarget) this.overlays.close();
  }
}
