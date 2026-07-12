import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import type { CliType } from '../../../../models/task.model';
import type { ProjectSourceDescriptor, ProjectSourceType } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { CliCatalogStore } from '../../../../services/cli-catalog.store';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';
import { RemoteHostsService } from '../../../remote-hosts';

const COLORS = ['#569cd6', '#4ec9b0', '#c586c0', '#d97757', '#f59e0b', '#8b5cf6'];

function deriveCode(value: string): string {
  const words = value.trim().split(/[^A-Za-z0-9]+/).filter(Boolean);
  if (words.length === 0) return '';
  const seed = words.length === 1
    ? words[0].slice(0, 3)
    : words.slice(0, 3).map(w => w[0]).join('');
  return seed.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 6);
}

@Component({
  selector: 'app-onboard-project-dialog',
  standalone: true,
  imports: [FormsModule, DialogComponent, CliModelSelectorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './onboard-project-dialog.component.html',
  styleUrl: './onboard-project-dialog.component.scss',
})
export class OnboardProjectDialogComponent {
  readonly manager = inject(WorkspaceManagerService);
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly catalog = inject(CliCatalogStore);
  readonly hostRegistry = inject(RemoteHostsService);

  readonly swatches = COLORS;
  readonly workspaceId = signal('');
  readonly displayName = signal('');
  readonly shortCode = signal('');
  readonly userEditedCode = signal(false);
  readonly cliDefault = signal<CliType>('claude');
  readonly modelDefault = signal('');
  readonly color = signal(COLORS[0]);
  /**
   * Optional CLI working directory. Without this, the project has no
   * auto-pickup runner until someone sets it later - the mode toggle then
   * fails with a "no RootPath configured" error instead of "unknown
   * project", but only once RunnerEndpoints knows to say so (see the
   * 2026-07-05 "Agent Studio" incident).
   */
  readonly rootPath = signal('');
  readonly repositoryUrl = signal('');
  readonly executionRunner = signal('local');
  readonly sourceType = signal<ProjectSourceType>('local-folder');
  readonly projectSources = signal<readonly ProjectSourceDescriptor[]>([]);
  readonly submitting = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly registryWorkspaces = signal<readonly { id: string; displayName: string; projects: readonly unknown[] }[]>([]);

  @ViewChild('nameInput') private nameInput?: ElementRef<HTMLInputElement>;

  readonly currentWorkspaceName = computed(() => {
    const id = this.workspaceId();
    for (const ws of this.registryWorkspaces()) {
      if (ws.id === id) return ws.displayName;
    }
    return id || 'Workspace';
  });

  readonly models = computed(() => this.catalog.modelsFor(this.cliDefault()));
  readonly previewProjectId = computed(() => `PROJ-${String(this.registryWorkspaces().flatMap(ws => ws.projects).length + 1).padStart(3, '0')}`);
  readonly canSubmit = computed(() =>
    !this.submitting() &&
    this.workspaceId().trim().length > 0 &&
    this.displayName().trim().length > 0 &&
    /^[A-Z][A-Z0-9]{1,5}$/.test(this.shortCode()),
  );

  constructor() {
    effect(() => {
      if (!this.manager.onboardProjectOpen()) return;
      const ws = this.manager.onboardWorkspaceId() ?? '';
      this.workspaceId.set(ws);
      this.displayName.set('');
      this.shortCode.set('');
      this.userEditedCode.set(false);
      this.cliDefault.set('claude');
      this.modelDefault.set('');
      this.color.set(COLORS[0]);
      this.rootPath.set('');
      this.repositoryUrl.set('');
      this.executionRunner.set('local');
      this.sourceType.set('local-folder');
      this.errorMsg.set(null);
      this.tasks.getRegistryWorkspaces().subscribe({
        next: list => this.registryWorkspaces.set(list ?? []),
        error: () => this.registryWorkspaces.set([]),
      });
      this.tasks.getProjectSources().subscribe({ next: sources => this.projectSources.set(sources), error: () => this.projectSources.set([]) });
      this.catalog.ensure('claude').subscribe({ error: () => void 0 });
      this.hostRegistry.ensureLoaded();
      queueMicrotask(() => this.nameInput?.nativeElement.focus());
    });
    effect(() => {
      const cli = this.cliDefault();
      this.catalog.ensure(cli).subscribe({ error: () => void 0 });
      const first = this.catalog.modelsFor(cli)[0]?.id ?? '';
      if (!this.modelDefault()) this.modelDefault.set(first);
    });
  }

  onNameChange(value: string): void {
    this.displayName.set(value);
    if (!this.userEditedCode()) this.shortCode.set(deriveCode(value));
  }

  onCodeChange(value: string): void {
    this.userEditedCode.set(true);
    this.shortCode.set(value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 6));
  }

  onAgentCommit(selection: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    this.cliDefault.set(selection.cliType);
    this.modelDefault.set(selection.model);
  }

  onCancel(): void {
    if (!this.submitting()) this.manager.closeProjectOnboard();
  }

  onSubmit(): void {
    if (!this.canSubmit()) return;
    const payload = {
      workspaceId: this.workspaceId(),
      sourceType: this.sourceType(),
      displayName: this.displayName().trim(),
      shortCode: this.shortCode(),
      cliDefault: this.cliDefault(),
      modelDefault: this.modelDefault() || null,
      color: this.color(),
      rootPath: this.rootPath().trim() || undefined,
      repositoryPath: this.rootPath().trim() || undefined,
      repositoryUrl: this.repositoryUrl().trim() || undefined,
      executionRunner: this.executionRunner() === 'local' ? undefined : this.executionRunner(),
    };
    this.submitting.set(true);
    this.errorMsg.set(null);
    this.tasks.createRegistryProject(payload).subscribe({
      next: project => {
        this.submitting.set(false);
        this.notifications.success(`Project "${project.displayName}" created.`);
        this.manager.refreshAfterProjectCreate();
      },
      error: err => {
        this.submitting.set(false);
        const msg = formatError(err);
        this.errorMsg.set(msg);
        this.notifications.error(`Could not create project: ${msg}`);
      },
    });
  }
}

function formatError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as { error?: string } | null;
    if (body?.error) return body.error;
    if (err.status === 0) return 'Backend unreachable.';
    return `Create failed (HTTP ${err.status}).`;
  }
  return 'Create failed.';
}
