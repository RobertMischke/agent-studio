import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective, type StructuredTooltip } from 'coding-agent-chat/shared';
import { buildModelLevelPresentation } from './model-level-indicator.util';

@Component({
  selector: 'app-model-level-indicator',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './model-level-indicator.component.html',
  styleUrl: './model-level-indicator.component.scss',
})
export class ModelLevelIndicatorComponent {
  readonly model = input<string | null>(null);
  readonly cliType = input<string | null>(null);
  readonly thinkingLevel = input<string | null>(null);
  readonly thinkingLevelOverride = input(false);
  readonly fallbackLabel = input('');
  readonly tooltip = input<string | StructuredTooltip>('');
  readonly source = input<string | null>(null);
  readonly isDefault = input(false);
  readonly testId = input('model-level-indicator');
  readonly levelTestId = input('model-level-thinking');

  readonly presentation = computed(() => buildModelLevelPresentation(
    this.model(),
    this.thinkingLevel(),
    this.fallbackLabel(),
  ));

  readonly accessibleLabel = computed(() => {
    const parts = [`Model ${this.model() || this.fallbackLabel() || 'unknown'}`];
    if (this.thinkingLevel()) parts.push(`thinking level ${this.thinkingLevel()}`);
    if (this.cliType()) parts.push(`CLI ${this.cliType()}`);
    return parts.join(', ');
  });
}
