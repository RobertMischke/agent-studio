import type { AspectVerdictTone } from '../../../../../components/aspect-findings';
import type { StructuredTooltip, TooltipSeverity } from 'coding-agent-chat/shared';
import type { ProtocolVerdict } from '../../protocol-pane/protocol-verdict';
import { LANE_PRESENTATIONS } from '../../../../../models/lane-presentation';

export interface DecisionBadgeVm {
  verdict: string;
  label: string;
  tone: AspectVerdictTone;
  severity: TooltipSeverity;
  tooltip: StructuredTooltip;
  toneToken?: string;
  toneValue?: string;
}

export function outcomeDecisionBadge(outcome: ProtocolVerdict | null): DecisionBadgeVm | null {
  if (!outcome) return null;
  const laneSignal = outcome.signals.find((candidate) => candidate.source === 'lane' && candidate.label === outcome.label);
  const presentation = laneSignal
    ? Object.values(LANE_PRESENTATIONS).find((item) => item.displayName === laneSignal.label)
    : null;
  const status = outcome.status;
  const tone: AspectVerdictTone = status === 'failed'
    ? 'danger'
    : status === 'succeeded' ? 'ok' : status === 'needs-decision' ? 'warn' : 'neutral';
  const severity: TooltipSeverity = tone === 'danger' ? 'error' : tone === 'warn' ? 'warn' : tone === 'ok' ? 'success' : 'info';
  return {
    verdict: status,
    label: outcome.label,
    tone,
    severity,
    toneToken: presentation?.toneToken,
    toneValue: presentation ? `var(${presentation.toneToken})` : undefined,
    tooltip: { title: `Run outcome: ${outcome.label}`, body: outcome.detail },
  };
}
