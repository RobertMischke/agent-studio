import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PromptDetail } from '../../../../services/prompt-admin.service';

@Component({
  selector: 'app-prompt-call-telemetry',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-call-telemetry.component.html',
  styleUrl: './prompt-call-telemetry.component.scss',
})
export class PromptCallTelemetryComponent {
  readonly detail = input.required<PromptDetail>();

  readonly maxDailyCalls = computed(() =>
    Math.max(1, ...this.detail().calls.daily.map(day => day.calls))
  );

  barHeight(calls: number): string {
    if (calls === 0) return '2px';
    return `${Math.max(8, Math.round((calls / this.maxDailyCalls()) * 100))}%`;
  }

  shortVersion(version: string): string {
    return version.slice(0, 10);
  }

  cost(value: number, unpricedCalls: number, totalCalls: number): string {
    if (totalCalls > 0 && unpricedCalls === totalCalls) return 'Unknown';
    const digits = value >= 1 ? 2 : value >= 0.01 ? 4 : 6;
    return `$${value.toFixed(digits)}${unpricedCalls > 0 ? '*' : ''}`;
  }
}
