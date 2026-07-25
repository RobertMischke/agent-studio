import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ComponentRoutingResolution } from '../../../../models/task.model';

@Component({
  selector: 'app-routing-preview',
  standalone: true,
  templateUrl: './routing-preview.component.html',
  styleUrl: './routing-preview.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoutingPreviewComponent {
  readonly routing = input<ComponentRoutingResolution | null>(null);
  readonly pending = input(false);
}
