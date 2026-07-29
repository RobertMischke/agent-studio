import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { PromptDetail } from '../../../../services/prompt-admin.service';

@Component({
  selector: 'app-prompt-review-section',
  standalone: true,
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-review-section.component.html',
  styleUrl: './prompt-review-section.component.scss',
})
export class PromptReviewSectionComponent {
  readonly detail = input.required<PromptDetail>();
  readonly busy = input(false);
  readonly reviewRequest = output<void>();

  statusLabel(status: string | null | undefined): string {
    if (status === 'needs-review') return 'needs review';
    return status ?? 'not reviewed';
  }
}
