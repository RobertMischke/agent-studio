import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ComponentOwnershipMapping, RegistryProjectSummary } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';

@Component({
  selector: 'app-ownership-mapping-panel',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './ownership-mapping-panel.component.html',
  styleUrl: './ownership-mapping-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OwnershipMappingPanelComponent implements OnInit {
  private readonly tasks = inject(TaskService);
  readonly projectName = input.required<string>();
  readonly project = signal<RegistryProjectSummary | null>(null);
  readonly mappings = signal<ComponentOwnershipMapping[]>([]);
  readonly savingId = signal<string | null>(null);
  readonly message = signal<string | null>(null);

  ngOnInit(): void { this.reload(); }

  addMapping(): void {
    const project = this.project();
    if (!project) return;
    this.mappings.update(rows => [...rows, {
      id: `component-${Date.now()}`, observedSurfaces: [], component: '', packageOrModule: null,
      primaryProjectId: project.id, repository: null, consumerProjectIds: [], integrationHosts: [],
      releaseArtifact: null, versioningMechanism: null, deploymentSteps: [], environments: [],
      allowedTicketPrefix: project.shortCode, evidence: [], confidence: 1, unresolvedAlternatives: [],
      version: 0, updatedAt: '', updatedBy: 'unsaved',
    }]);
  }

  save(mapping: ComponentOwnershipMapping): void {
    const project = this.project();
    if (!project) return;
    this.savingId.set(mapping.id);
    this.tasks.updateOwnershipMapping(project.id, mapping.id, mapping).subscribe({
      next: (updated) => {
        this.mappings.update(rows => rows.map(row => row.id === updated.id ? structuredClone(updated) : row));
        this.savingId.set(null);
        this.message.set(`Saved ${updated.id} as version ${updated.version}.`);
      },
      error: (error) => {
        this.savingId.set(null);
        this.message.set(error?.error?.error || 'Could not save ownership mapping.');
      },
    });
  }

  updateList(mapping: ComponentOwnershipMapping, field: 'observedSurfaces' | 'consumerProjectIds' | 'integrationHosts' | 'deploymentSteps' | 'environments' | 'evidence' | 'unresolvedAlternatives', value: string): void {
    mapping[field] = value.split('\n').map(item => item.trim()).filter(Boolean);
  }

  list(mapping: ComponentOwnershipMapping, field: 'observedSurfaces' | 'consumerProjectIds' | 'integrationHosts' | 'deploymentSteps' | 'environments' | 'evidence' | 'unresolvedAlternatives'): string {
    return mapping[field].join('\n');
  }

  private reload(): void {
    this.tasks.getRegistryWorkspaces({ includeArchived: true }).subscribe((workspaces) => {
      const project = workspaces.flatMap(workspace => workspace.projects)
        .find(row => row.displayName === this.projectName() || row.id === this.projectName());
      this.project.set(project ?? null);
      this.mappings.set((project?.ownershipMappings ?? []).map(mapping => structuredClone(mapping)));
    });
  }
}
