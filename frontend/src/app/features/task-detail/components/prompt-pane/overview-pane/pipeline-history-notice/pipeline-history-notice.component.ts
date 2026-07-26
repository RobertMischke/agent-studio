import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'app-pipeline-history-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-history-notice.component.html',
  styleUrl: './pipeline-history-notice.component.scss',
})
export class PipelineHistoryNoticeComponent {
  readonly attempt = input.required<number>();
  readonly currentAttempt = input.required<number>();
}
