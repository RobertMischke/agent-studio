import type { PipelineCostSummary, StepKind } from '../../../../task-pipeline';
import { stepKindLabel } from '../../../../task-pipeline';
import { formatTokenCostDisplay } from '../../../../tokens';

export interface TypeBreakdownRow {
  kind: StepKind;
  label: string;
  totalTokens: number;
  costLabel: string;
}

/**
 * Reading order for the by-type section: the coding run first, then the
 * review/gate/enrichment passes that judge it, oldest-established-concept
 * last. Kinds absent from this run (zero recorded tokens) are dropped
 * rather than shown as a quiet zero row.
 */
const KIND_ORDER: readonly StepKind[] = ['core', 'aspect', 'orchestrator', 'drift', 'module', 'tool'];

/**
 * Groups a job's already-dated, already-priced per-step costs
 * ({@link PipelineCostSummary} from `GET /tasks/{id}/pipeline`, computed by
 * the shared `PipelineCostCalculator`) by {@link StepKind}. Pure aggregation;
 * no pricing logic lives here.
 */
export function buildTypeBreakdown(cost: PipelineCostSummary): TypeBreakdownRow[] {
  const byKind = new Map<StepKind, { totalTokens: number; costUsd: number; modelKnown: boolean }>();
  for (const step of cost.steps) {
    if (step.totalTokens <= 0) continue;
    const bucket = byKind.get(step.kind) ?? { totalTokens: 0, costUsd: 0, modelKnown: true };
    bucket.totalTokens += step.totalTokens;
    bucket.costUsd += step.costUsd;
    bucket.modelKnown = bucket.modelKnown && step.modelKnown;
    byKind.set(step.kind, bucket);
  }
  return KIND_ORDER
    .filter((kind) => byKind.has(kind))
    .map((kind) => {
      const bucket = byKind.get(kind)!;
      return {
        kind,
        label: stepKindLabel(kind),
        totalTokens: bucket.totalTokens,
        costLabel: formatTokenCostDisplay({
          costUsd: bucket.costUsd,
          totalTokens: bucket.totalTokens,
          unpricedRuns: bucket.modelKnown ? 0 : 1,
        }),
      };
    });
}
