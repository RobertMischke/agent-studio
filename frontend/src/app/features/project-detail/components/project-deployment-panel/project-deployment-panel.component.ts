import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import type { CompiledDeploymentPrompt, ProjectDeploymentSummary, ProjectDeploymentTarget } from '../../../../models/project-overview.model';
import { VisibleCliTaskService, type VisibleCliTaskCreated } from '../../../visible-cli-task';
import type { WatchPathEntry } from '../../../../models/task.model';

@Component({
  selector: 'app-project-deployment-panel',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule],
  templateUrl: './project-deployment-panel.component.html',
  styleUrl: './project-deployment-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeploymentPanelComponent {
  private readonly tasks = inject(TaskService);
  private readonly cliTasks = inject(VisibleCliTaskService);

  readonly projectName = input.required<string>();
  readonly summary = signal<ProjectDeploymentSummary | null>(null);
  readonly loading = signal(true);
  readonly requestFailed = signal(false);
  readonly workspaces = signal<WatchPathEntry[]>([]);
  readonly selectedTarget = signal<ProjectDeploymentTarget | null>(null);
  readonly parameterValues = signal<Record<string, string | boolean>>({});
  readonly running = signal(false);
  readonly runError = signal<string | null>(null);
  readonly createdTask = signal<VisibleCliTaskCreated | null>(null);
  readonly prompt = signal('');
  readonly compiling = signal(false);
  readonly compiled = signal<CompiledDeploymentPrompt | null>(null);

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  refresh(): void {
    this.load(this.projectName());
  }

  chooseTarget(target: ProjectDeploymentTarget): void {
    this.selectedTarget.set(target);
    this.parameterValues.set(Object.fromEntries(target.parameters.map(parameter => [
      parameter.name,
      typeof parameter.default === 'boolean' || typeof parameter.default === 'string'
        ? parameter.default
        : parameter.type === 'boolean' ? false : '',
    ])));
    this.createdTask.set(null);
    this.runError.set(null);
  }

  setParameter(name: string, value: string | boolean): void {
    this.parameterValues.update(current => ({ ...current, [name]: value }));
  }

  compilePrompt(): void {
    if (!this.prompt().trim() || this.compiling()) return;
    this.compiling.set(true);
    this.tasks.compileProjectDeployment(this.projectName(), this.prompt()).subscribe({
      next: compiled => {
        this.compiled.set(compiled);
        this.compiling.set(false);
        if (compiled.runnable && compiled.command) {
          this.chooseTarget({ id: 'prompt-preview', kind: 'prompt', template: null, source: 'prompt-preview', targetHostId: null, ...compiled });
        }
      },
      error: () => {
        this.compiled.set(null);
        this.compiling.set(false);
      },
    });
  }

  canRun(target: ProjectDeploymentTarget): boolean {
    const values = this.parameterValues();
    return !this.createdTask() && target.runnable && !!target.command && target.parameters.every(parameter =>
      !parameter.required || values[parameter.name] === true || String(values[parameter.name] ?? '').trim().length > 0);
  }

  runSelected(): void {
    const target = this.selectedTarget();
    const workspace = this.workspaces().find(item => item.name.toLowerCase() === this.projectName().toLowerCase());
    if (!target || !workspace || !this.canRun(target) || this.running()) return;
    const values = this.parameterValues();
    const command = target.command!.replace(/\{\{([A-Za-z][A-Za-z0-9_-]*)\}\}/g, (_, name: string) => shellValue(values[name]));
    this.running.set(true);
    this.runError.set(null);
    this.cliTasks.start({
      title: `Deploy ${target.title}`,
      scope: `Deploy ${target.title}`,
      reason: target.summary,
      command,
      prompt: [
        `Run deployment target \`${target.id}\` using the exact command in the execution contract.`,
        'Report preflight checks, each command outcome, the deployed revision, and the final health check in this task conversation.',
        'Do not request or copy secrets. Any secret reference must resolve on the execution host.',
      ].join('\n\n'),
      context: { deploymentTarget: target.id, source: target.source, ...Object.fromEntries(Object.entries(values).map(([key, value]) => [key, String(value)])) },
      cliType: 'codex',
    }, workspace.path).subscribe({
      next: task => this.createdTask.set(task),
      error: error => {
        this.runError.set(error?.error?.message ?? 'The deployment task could not be created.');
        this.running.set(false);
      },
      complete: () => this.running.set(false),
    });
  }

  shortSha(sha: string): string {
    return sha.slice(0, 8);
  }

  private load(projectName: string): void {
    this.loading.set(true);
    this.requestFailed.set(false);
    this.tasks.getProjectDeploymentSummary(projectName).subscribe({
      next: summary => {
        this.summary.set(summary);
        const current = this.selectedTarget();
        const next = summary.targets.find(target => target.id === current?.id) ?? summary.targets[0] ?? null;
        if (next) this.chooseTarget(next);
        this.loading.set(false);
      },
      error: () => {
        this.summary.set(null);
        this.requestFailed.set(true);
        this.loading.set(false);
      },
    });
    this.tasks.getWatchPaths().subscribe({ next: paths => this.workspaces.set(paths) });
  }
}

function shellValue(value: string | boolean | undefined): string {
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  return `'${String(value ?? '').replace(/'/g, `'"'"'`)}'`;
}
