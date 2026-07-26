import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { PromptCoverageResponse } from '../../../../services/prompt-admin.service';

@Component({
  selector: 'app-prompt-coverage-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-coverage-section.component.html',
  styleUrl: './prompt-coverage-section.component.scss',
})
export class PromptCoverageSectionComponent {
  readonly coverage = input.required<PromptCoverageResponse>();
}
