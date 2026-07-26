import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { ModelRoutingRecommendation } from '../../../quota';

@Component({
  selector: 'app-model-routing-suggestion',
  standalone: true,
  templateUrl: './model-routing-suggestion.component.html',
  styleUrl: './model-routing-suggestion.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModelRoutingSuggestionComponent {
  readonly suggestion = input.required<ModelRoutingRecommendation>();
  readonly explicit = input(false);
  readonly usePolicy = output<void>();
}
