import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobService } from '../services/job.service';

interface AutonomyStop {
  level: number;
  name: string;
  desc: string;
}

const STOPS: AutonomyStop[] = [
  { level: 0, name: 'Manual', desc: 'Orchestrator never moves a task forward without a human click. Queue may be empty.' },
  { level: 1, name: 'Cautious', desc: 'Orchestrator may sharpen prompts once. Every borderline task is bounced to NeedsClar for clarification.' },
  { level: 2, name: 'Balanced', desc: 'Default. Orchestrator iterates up to 3 times and decides on clear cases. Bounces only the genuinely-unclear.' },
  { level: 3, name: 'Confident', desc: 'Orchestrator decides on most cases. Cap-exit raises a supervisor advisory instead of bouncing.' },
  { level: 4, name: 'Fully auto', desc: 'Never bounces. Unclear tasks ship to Ready with a [supervisor] chat-note. Queue is never allowed to stall on ambiguity.' },
];

/**
 * ADR-0026: per-project orchestrator-prep autonomy slider. Persists to
 * `/api/projects/{name}/autonomy` on every change. The next pickup tick
 * honours the new value; in-flight prep iterations finish under the old
 * value (no mid-iteration policy switch).
 */
@Component({
  selector: 'app-autonomy-slider',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="autonomy" data-testid="autonomy-slider">
      <header>
        <h3>Orchestrator autonomy</h3>
        <span class="autonomy__current" [attr.data-testid]="'autonomy-level-current'">
          {{ currentStop().level }}: {{ currentStop().name }}
        </span>
      </header>
      <input type="range"
             class="autonomy__range"
             min="0" max="4" step="1"
             data-testid="autonomy-range"
             [ngModel]="level()"
             (ngModelChange)="onChange($event)" />
      <div class="autonomy__stops">
        @for (s of stops; track s.level) {
          <span [class.autonomy__stop--active]="s.level === level()" [title]="s.desc">{{ s.name }}</span>
        }
      </div>
      <p class="autonomy__desc">{{ currentStop().desc }}</p>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .autonomy { padding: 10px 12px; background: rgba(255,255,255,0.03); border: 1px solid rgba(255,255,255,0.08); border-radius: 7px; }
    .autonomy header { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 8px; }
    .autonomy h3 { margin: 0; font-size: 0.85rem; color: #cdd6f4; text-transform: uppercase; letter-spacing: 0.04em; }
    .autonomy__current { color: #cba6f7; font-size: 0.85rem; font-weight: 600; }
    .autonomy__range { width: 100%; }
    .autonomy__stops { display: grid; grid-template-columns: repeat(5, 1fr); font-size: 0.7rem; color: #7f849c; text-transform: uppercase; letter-spacing: 0.04em; margin-top: 4px; }
    .autonomy__stops span { text-align: center; cursor: help; }
    .autonomy__stop--active { color: #cba6f7; font-weight: 600; }
    .autonomy__desc { color: #a6adc8; font-size: 0.78rem; margin: 8px 0 0; line-height: 1.4; }
  `]
})
export class AutonomySliderComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly stops = STOPS;

  private readonly jobService = inject(JobService);

  readonly level = signal<number>(2);

  readonly currentStop = computed(() => STOPS[this.level()] ?? STOPS[2]);

  ngOnInit() {
    this.jobService.getProjectAutonomyLevel(this.projectName()).subscribe({
      next: (resp) => this.level.set(this.clamp(resp?.level ?? 2)),
      error: () => { /* keep default; backend may be older */ }
    });
  }

  onChange(value: number) {
    const clamped = this.clamp(Number(value));
    if (clamped === this.level()) return;
    this.level.set(clamped);
    this.jobService.setProjectAutonomyLevel(this.projectName(), clamped).subscribe({
      error: () => { /* roll back not strictly needed; the next read will reconcile */ }
    });
  }

  private clamp(n: number) {
    if (Number.isNaN(n)) return 2;
    return Math.max(0, Math.min(4, Math.round(n)));
  }
}
