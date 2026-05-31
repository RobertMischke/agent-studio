import { ChangeDetectionStrategy, Component } from '@angular/core';
import { PlanStripComponent } from '../../../app/features/plan-strip/plan-strip.component';
import type { TaskPlanView } from '../../../app/features/plan-strip/plan.model';

/** Recent ISO timestamp so the active item's heartbeat renders as "pulsing". */
const recent = (secondsAgo: number) => new Date(Date.now() - secondsAgo * 1000).toISOString();

function sub(tool: string, label: string, secondsAgo: number) {
  return { ts: recent(secondsAgo), tool, label };
}

/**
 * A live Claude run: two items finished, one active with work in flight,
 * two still pending. Exercises all four cues at once - ticker, latest
 * label, soft-estimate band (median of the two finished items), heartbeat.
 */
const ACTIVE_CLAUDE: TaskPlanView = {
  hasPlan: true,
  source: 'claude/TodoWrite',
  snapshotCount: 6,
  activeItemId: 'item-c',
  softEstimateMedian: 3,
  items: [
    {
      id: 'item-a',
      title: 'Survey the repository layout',
      status: 'done',
      subActionCount: 3,
      subActions: [
        sub('Read', 'Read AGENTS.md', 280),
        sub('Grep', 'Grep "PlanReader"', 274),
        sub('Read', 'Read PlanReader.cs', 268),
      ],
    },
    {
      id: 'item-b',
      title: 'Add the typed PlanUpdated event',
      status: 'done',
      subActionCount: 4,
      subActions: [
        sub('Read', 'Read CliRunEvent.cs', 240),
        sub('Edit', 'Edit CliRunEvent.cs', 232),
        sub('Edit', 'Edit ClaudeEventAdapter.cs', 221),
        sub('Bash', 'dotnet build', 210),
      ],
    },
    {
      id: 'item-c',
      title: 'Write the plan-snapshots replay reader',
      status: 'active',
      subActionCount: 5,
      subActions: [
        sub('Read', 'Read AgentWorkSummaryReader.cs', 60),
        sub('Write', 'Write PlanReader.cs', 44),
        sub('Edit', 'Edit JobModels.cs', 30),
        sub('Edit', 'Edit JobRunnerEndpoints.cs', 18),
        sub('Bash', 'dotnet build backend', 4),
      ],
    },
    { id: 'item-d', title: 'Cover the reader with unit tests', status: 'pending', subActionCount: 0, subActions: [] },
    { id: 'item-e', title: 'Wire the frontend plan strip', status: 'pending', subActionCount: 0, subActions: [] },
  ],
  unassignedSubActions: [sub('Read', 'Read prompt.md', 300), sub('Bash', 'git status', 296)],
};

/** A finished Codex run: every item done, each expandable to its sub-actions. */
const DONE_CODEX: TaskPlanView = {
  hasPlan: true,
  source: 'codex/update_plan',
  snapshotCount: 4,
  activeItemId: null,
  softEstimateMedian: 3,
  items: [
    {
      id: 'd-a',
      title: 'Reproduce the failing migration',
      status: 'done',
      subActionCount: 2,
      subActions: [sub('command_call', 'dotnet ef database update', 500), sub('Read', 'Read 0042_schema.sql', 490)],
    },
    {
      id: 'd-b',
      title: 'Patch the backfill default',
      status: 'done',
      subActionCount: 3,
      subActions: [
        sub('Read', 'Read UserSchema.cs', 470),
        sub('file_change', 'UserSchema.cs', 460),
        sub('command_call', 'dotnet test', 440),
      ],
    },
    {
      id: 'd-c',
      title: 'Verify under concurrent writes',
      status: 'done',
      subActionCount: 4,
      subActions: [
        sub('command_call', 'dotnet run --project loadtest', 400),
        sub('Read', 'Read results.json', 360),
        sub('file_change', 'CHANGELOG.md', 350),
        sub('command_call', 'git commit', 340),
      ],
    },
  ],
  unassignedSubActions: [],
};

@Component({
  selector: 'mockup-plan-strip-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PlanStripComponent],
  templateUrl: './plan-strip-gallery.component.html',
  styleUrl: './plan-strip-gallery.component.scss',
})
export class PlanStripGalleryComponent {
  readonly active = ACTIVE_CLAUDE;
  readonly done = DONE_CODEX;
}
