import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { OrchestratorContextReceipt } from '../../models/orchestrator.model';

@Component({
  selector: 'app-orchestrator-context-receipt',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-context-receipt.component.html',
  styleUrl: './orchestrator-context-receipt.component.scss',
})
export class OrchestratorContextReceiptComponent {
  readonly receipt = input.required<OrchestratorContextReceipt>();
}
