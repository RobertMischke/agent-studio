import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { PipelineType } from '../../../../task-pipeline';
import { PIPELINE_TYPES } from '../pipeline-config.util';

@Component({
  selector: 'app-pipeline-type-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-type-picker.component.html',
  styleUrl: './pipeline-type-picker.component.scss',
})
export class PipelineTypePickerComponent {
  readonly value = input.required<PipelineType>();
  readonly valueChange = output<PipelineType>();
  readonly types = PIPELINE_TYPES;

  onChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const selected = this.types.find(type => type.id === value);
    if (selected) this.valueChange.emit(selected.id);
  }
}
