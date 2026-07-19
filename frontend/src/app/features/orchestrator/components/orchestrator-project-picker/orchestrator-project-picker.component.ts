import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

@Component({
  selector: 'app-orchestrator-project-picker',
  standalone: true,
  templateUrl: './orchestrator-project-picker.component.html',
  styleUrl: './orchestrator-project-picker.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrchestratorProjectPickerComponent {
  readonly projects = input<readonly string[]>([]);
  readonly activeProject = input<string | null>(null);
  readonly disabled = input(false);
  readonly projectSelected = output<string>();
  readonly open = signal(false);
  readonly query = signal('');
  readonly highlight = signal(0);
  readonly filtered = computed(() => {
    const query = this.query().trim().toLowerCase();
    return query ? this.projects().filter(project => project.toLowerCase().includes(query)) : [...this.projects()];
  });

  onInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement | null)?.value ?? '');
    this.open.set(true);
    this.highlight.set(0);
  }

  onBlur(): void {
    setTimeout(() => { this.open.set(false); this.query.set(''); }, 120);
  }

  onKeydown(event: KeyboardEvent): void {
    const list = this.filtered();
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!list.length) return;
      const delta = event.key === 'ArrowDown' ? 1 : -1;
      this.highlight.update(index => (index + delta + list.length) % list.length);
      this.open.set(true);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const project = list[this.highlight()] ?? list[0];
      if (project) this.select(project);
    } else if (event.key === 'Escape') {
      this.open.set(false);
      this.query.set('');
      (event.target as HTMLInputElement | null)?.blur();
    }
  }

  select(project: string): void {
    this.projectSelected.emit(project);
    this.open.set(false);
    this.query.set('');
  }
}
