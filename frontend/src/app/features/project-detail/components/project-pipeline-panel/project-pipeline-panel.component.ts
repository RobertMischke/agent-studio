import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type {
  PipelineCatalogueStep,
  PipelineStepSetting,
  PipelineStepCondition,
  PipelineStepConditionToken,
} from '../../../task-pipeline';
import type { ProjectPipelineCostTimeline } from '../../../project-token-usage';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { TooltipDirective } from '../../../../components/tooltip';
import {
  PIPELINE_GATE_MODES,
  PIPELINE_CONDITIONS,
  PIPELINE_CONDITION_VALUE_TOKENS,
  type PipelineAdminRow,
  type PipelineGroup,
  type PipelineKindLegendRow,
  kindLabel,
  phaseForStep,
  pipelinePhaseLabel,
  pipelineOrderSection,
  orderedPipelineCatalogue,
  canMovePipelineStep,
  formatCost,
  formatTokens,
} from './pipeline-config.util';

/**
 * Project-level Pipeline page (Nav-rebuild step 3 / T4a). Renders the
 * pre/core/post step catalogue as a calm CSS grid where each configurable
 * step exposes activation, ordering, its per-step LLM model, a prompt
 * *binding reference* (content is managed in the Prompts registry, never
 * inline here), and the gate / run-condition controls. A compact "cost by
 * step kind" rollup sits below so the operator sees what the configuration
 * actually spends.
 *
 * All writes go through `setProjectPipelineStep` /
 * `setProjectPipelineStepOrder`; the same backend contract the old Project
 * Settings pipeline block used, so every existing configuration stays
 * operable at the new location.
 */
@Component({
  selector: 'app-project-pipeline-panel',
  standalone: true,
  imports: [FormsModule, CliModelSelectorComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-pipeline-panel.component.html',
  styleUrl: './project-pipeline-panel.component.scss',
})
export class ProjectPipelinePanelComponent {
  readonly projectName = input.required<string>();

  /** Deep-link to the Prompts registry rail (content is managed there). */
  readonly openPrompts = output<void>();

  private readonly jobService = inject(TaskService);

  readonly catalogue = signal<readonly PipelineCatalogueStep[]>([]);
  readonly overrides = signal<Record<string, PipelineStepSetting>>({});
  readonly order = signal<readonly string[]>([]);
  readonly pipelineCost = signal<ProjectPipelineCostTimeline | null>(null);
  readonly loadError = signal<string | null>(null);

  readonly cliTypes = CLI_TYPES;
  readonly gateModes = PIPELINE_GATE_MODES;
  readonly conditions = PIPELINE_CONDITIONS;

  /** Per-step write in flight; disables that row's controls until the PUT resolves. */
  readonly stepBusy: Record<string, boolean> = {};
  readonly orderBusy = signal(false);

  /**
   * In-progress condition edits, keyed by step id. Shadows the persisted
   * condition so a value-bearing token (task-type / tag) can show its value
   * input before a value has been entered and persisted.
   */
  readonly conditionDraft = signal<Record<string, { when: string; value: string }>>({});

  constructor() {
    effect(() => {
      const name = this.projectName();
      if (name) this.load(name);
    });
  }

  /**
   * One row per configurable step: catalogue metadata joined with the
   * project's override. No override (or null `enabled`) falls back to the
   * step's `defaultEnabled`. Empty model/mode = inherit.
   */
  readonly rows = computed<PipelineAdminRow[]>(() => {
    const overrides = this.overrides();
    const drafts = this.conditionDraft();
    const catalogue = orderedPipelineCatalogue(this.catalogue(), this.order());
    return catalogue.map((step, index) => {
      const ov = overrides[step.id];
      const draft = drafts[step.id];
      const conditionWhen = draft?.when ?? ov?.condition?.when ?? '';
      const conditionValue = draft?.value ?? ov?.condition?.value ?? '';
      return {
        id: step.id,
        displayName: step.displayName,
        kind: step.kind,
        usesModel: step.usesModel,
        usesPrompt: step.usesPrompt,
        supportsMode: step.supportsMode,
        canDisable: step.canDisable,
        supportsCondition: step.supportsCondition,
        phase: step.phase ?? phaseForStep(step),
        enabled: ov?.enabled ?? step.defaultEnabled,
        cliType: ov?.cliType ?? step.cliType ?? '',
        model: ov?.model ?? '',
        thinkingLevel: ov?.thinkingLevel ?? '',
        prompt: ov?.prompt ?? '',
        promptTemplate: step.promptTemplate ?? '',
        mode: ov?.mode ?? '',
        condition: conditionWhen,
        conditionValue,
        conditionNeedsValue: PIPELINE_CONDITION_VALUE_TOKENS.includes(conditionWhen),
        canMoveUp: canMovePipelineStep(catalogue, index, -1),
        canMoveDown: canMovePipelineStep(catalogue, index, 1),
      };
    });
  });

  readonly groups = computed<PipelineGroup[]>(() => {
    const groups: PipelineGroup[] = [];
    for (const row of this.rows()) {
      const phase = row.phase || 'post';
      let group = groups.find(g => g.phase === phase);
      if (!group) {
        group = { phase, label: pipelinePhaseLabel(phase), rows: [] };
        groups.push(group);
      }
      group.rows.push(row);
    }
    return groups;
  });

  readonly costLegend = computed<PipelineKindLegendRow[]>(() => {
    const t = this.pipelineCost();
    if (!t) return [];
    return t.kinds.map(k => ({
      kind: k.kind,
      label: kindLabel(k.kind),
      tokens: k.totalTokens,
      cost: k.totalCostUsd,
      anyUnknown: k.anyModelUnknown,
    }));
  });

  private load(project: string): void {
    this.jobService.getPipelineCatalogue().subscribe({
      next: (cat) => { this.catalogue.set(cat.steps ?? []); this.loadError.set(null); },
      error: () => this.loadError.set('Could not load the pipeline catalogue.'),
    });
    this.refreshOverrides(project);
    this.jobService.getProjectPipelineCost(project, 30).subscribe({
      next: (t) => this.pipelineCost.set(t),
      error: () => { /* cost is a secondary read; leave it null -> section hides */ },
    });
  }

  private refreshOverrides(project: string): void {
    this.jobService.getAllProjectSettings().subscribe({
      next: (all) => {
        this.overrides.set(all[project]?.pipelineSteps ?? {});
        this.order.set(all[project]?.pipelineStepOrder ?? []);
      },
      error: () => { /* keep last known overrides */ },
    });
  }

  onStepEnabledChange(stepId: string, enabled: boolean): void {
    this.writeStep(stepId, { enabled });
  }

  onStepMove(stepId: string, direction: -1 | 1): void {
    if (this.orderBusy()) return;
    const rows = orderedPipelineCatalogue(this.catalogue(), this.order());
    const index = rows.findIndex(step => step.id === stepId);
    if (index < 0) return;

    const section = pipelineOrderSection(rows[index]);
    if (section === 'core') return;

    let target = index + direction;
    while (target >= 0 && target < rows.length && pipelineOrderSection(rows[target]) !== section) {
      target += direction;
    }
    if (target < 0 || target >= rows.length) return;

    const next = [...rows];
    [next[index], next[target]] = [next[target], next[index]];
    const stepIds = next
      .filter(step => pipelineOrderSection(step) !== 'core')
      .map(step => step.id);

    this.orderBusy.set(true);
    this.order.set(stepIds);
    this.jobService.setProjectPipelineStepOrder(this.projectName(), stepIds).subscribe({
      next: (res) => {
        this.orderBusy.set(false);
        this.order.set(res.pipelineStepOrder ?? stepIds);
      },
      error: () => {
        this.orderBusy.set(false);
        this.refreshOverrides(this.projectName());
      },
    });
  }

  onStepModeChange(stepId: string, mode: string): void {
    this.writeStep(stepId, { mode });
  }

  /** Clear a legacy inline prompt override; content lives in the registry. */
  clearStepPrompt(stepId: string): void {
    this.writeStep(stepId, { prompt: '' });
  }

  resetStepAgent(stepId: string): void {
    this.writeStep(stepId, { cliType: '', model: '', thinkingLevel: null });
  }

  onStepAgentCommit(
    stepId: string,
    selection: { cliType: CliType; model: string; thinkingLevel: string | null },
  ): void {
    this.writeStep(stepId, {
      cliType: selection.cliType,
      model: selection.model,
      thinkingLevel: selection.thinkingLevel,
    });
  }

  /**
   * The condition <select> changed. A non-value token persists immediately;
   * a value-bearing token (task-type / tag) keeps a draft until a value
   * exists. Picking the empty option clears the condition.
   */
  onStepConditionChange(stepId: string, when: string): void {
    const existingValue = this.conditionDraft()[stepId]?.value
      ?? this.overrides()[stepId]?.condition?.value ?? '';

    if (!when) {
      this.setConditionDraft(stepId, { when: '', value: existingValue });
      this.writeStep(stepId, { condition: null });
      return;
    }

    if (PIPELINE_CONDITION_VALUE_TOKENS.includes(when)) {
      this.setConditionDraft(stepId, { when, value: existingValue });
      if (existingValue.trim()) {
        this.writeStep(stepId, { condition: { when: when as PipelineStepConditionToken, value: existingValue.trim() } });
      }
      return;
    }

    this.setConditionDraft(stepId, { when, value: '' });
    this.writeStep(stepId, { condition: { when: when as PipelineStepConditionToken } });
  }

  onStepConditionValueInput(stepId: string, value: string): void {
    const when = this.conditionDraft()[stepId]?.when
      ?? this.overrides()[stepId]?.condition?.when ?? '';
    this.setConditionDraft(stepId, { when, value });
  }

  onStepConditionValueCommit(stepId: string): void {
    const draft = this.conditionDraft()[stepId];
    const ov = this.overrides()[stepId];
    const when = draft?.when ?? ov?.condition?.when ?? '';
    const value = (draft?.value ?? ov?.condition?.value ?? '').trim();
    if (!when || !PIPELINE_CONDITION_VALUE_TOKENS.includes(when)) return;
    this.writeStep(stepId, {
      condition: value ? { when: when as PipelineStepConditionToken, value } : null,
    });
  }

  private setConditionDraft(stepId: string, draft: { when: string; value: string }): void {
    this.conditionDraft.update(m => ({ ...m, [stepId]: draft }));
  }

  private clearConditionDraft(stepId: string): void {
    this.conditionDraft.update(m => {
      if (!(stepId in m)) return m;
      const next = { ...m };
      delete next[stepId];
      return next;
    });
  }

  /**
   * Merge one changed facet onto the step's current override and PUT the
   * whole step (the backend replaces the entry, so unchanged facets are
   * resent). `enabled` is sent as null when it equals the built-in default
   * so an at-default step clears its entry instead of leaving a dead one.
   */
  private writeStep(
    stepId: string,
    patch: {
      enabled?: boolean;
      cliType?: string;
      model?: string;
      thinkingLevel?: string | null;
      prompt?: string | null;
      mode?: string;
      condition?: PipelineStepCondition | null;
    },
  ): void {
    const cur = this.overrides()[stepId] ?? {};
    const defaultEnabled = this.catalogue().find(s => s.id === stepId)?.defaultEnabled ?? true;
    const enabled = patch.enabled ?? (cur.enabled ?? defaultEnabled);
    const model = (patch.model ?? cur.model ?? '').trim();
    const cliType = (patch.cliType ?? cur.cliType ?? '').trim();
    const thinkingLevel = (patch.thinkingLevel !== undefined ? patch.thinkingLevel : (cur.thinkingLevel ?? ''))?.trim() ?? '';
    const prompt = (patch.prompt !== undefined ? patch.prompt : (cur.prompt ?? ''))?.trim() ?? '';
    const mode = (patch.mode ?? cur.mode ?? '').trim();
    const condition = patch.condition !== undefined ? patch.condition : (cur.condition ?? null);

    this.stepBusy[stepId] = true;
    this.jobService.setProjectPipelineStep(this.projectName(), {
      stepId,
      enabled: enabled === defaultEnabled ? null : enabled,
      cliType: cliType || null,
      model: model || null,
      thinkingLevel: thinkingLevel || null,
      prompt: prompt || null,
      mode: mode || null,
      condition: condition ?? null,
    }).subscribe({
      next: (res) => {
        this.stepBusy[stepId] = false;
        this.overrides.set(res.pipelineSteps ?? {});
        this.clearConditionDraft(stepId);
      },
      error: () => {
        this.stepBusy[stepId] = false;
        this.refreshOverrides(this.projectName());
        this.clearConditionDraft(stepId);
      },
    });
  }

  asCliType(value: string | null | undefined): CliType | null {
    return value && (CLI_TYPES as readonly string[]).includes(value) ? value as CliType : null;
  }

  formatCost = formatCost;
  formatTokens = formatTokens;
}
