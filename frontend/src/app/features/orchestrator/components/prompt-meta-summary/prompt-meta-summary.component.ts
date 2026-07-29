import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { PromptDetail } from '../../../../services/prompt-admin.service';

@Component({
  selector: 'app-prompt-meta-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-meta-summary.component.html',
  styleUrl: './prompt-meta-summary.component.scss',
})
export class PromptMetaSummaryComponent {
  readonly detail = input.required<PromptDetail>();
}
