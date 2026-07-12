import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { StudioIconComponent } from '../../../../../../components/studio-icon/studio-icon.component';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../../../models/project-docs.model';

@Component({
  selector: 'app-workbench-inbox',
  standalone: true,
  imports: [StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-inbox.component.html',
  styleUrl: './workbench-inbox.component.scss',
})
export class WorkbenchInboxComponent {
  readonly catalogue = input<WorkbenchCatalogue | null>(null);
  readonly openWorkbench = output<WorkbenchListItem>();

  relativeTime(iso: string): string {
    const ms = new Date(iso).getTime();
    if (Number.isNaN(ms)) return iso;
    const minutes = Math.max(0, Math.floor((Date.now() - ms) / 60_000));
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    return days < 30 ? `${days}d ago` : new Date(ms).toLocaleDateString();
  }
}
