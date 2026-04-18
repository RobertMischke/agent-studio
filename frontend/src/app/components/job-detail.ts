import { Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobDetail, WatchPathEntry } from '../models/job.model';
import { JobService } from '../services/job.service';

@Component({
  selector: 'app-job-detail',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="detail">
      <div class="detail__header">
        <button class="detail__back" (click)="back.emit()">←</button>
        <h2 class="detail__title">{{ detail().info.title || detail().info.id }}</h2>
        <span class="detail__state" [class]="'state--' + detail().info.state">
          {{ stateLabel(detail().info.state) }}
        </span>
      </div>

      <div class="detail__meta">
        <select class="detail__project-select"
                [ngModel]="detail().info.watchPath"
                (ngModelChange)="onProjectChange($event)">
          @for (wp of watchPaths(); track wp.path) {
            <option [value]="wp.path">{{ wp.name }}</option>
          }
        </select>
        <span>🤖 {{ detail().info.agent }}</span>
        <span>#{{ detail().info.order }}</span>
        <span>{{ formatDate(detail().info.createdAt) }}</span>
      </div>

      <section class="section">
        <div class="section__header">
          <h3 class="section__title">Prompt</h3>
          @if (!isProgress()) {
            @if (editingPrompt()) {
              <div class="section__actions">
                <button class="btn-sm" (click)="cancelEdit('prompt')">Cancel</button>
                <button class="btn-sm btn-sm--primary" (click)="saveFile('prompt.md', promptDraft())">Save</button>
              </div>
            } @else {
              <button class="btn-sm" (click)="startEdit('prompt')">✏️ Edit</button>
            }
          }
        </div>
        @if (editingPrompt()) {
          <textarea class="section__editor" [(ngModel)]="promptDraftValue" rows="10"></textarea>
        } @else {
          <pre class="section__body">{{ detail().promptMarkdown || '(empty)' }}</pre>
        }
      </section>

      <section class="section">
        <div class="section__header">
          <h3 class="section__title">Status</h3>
          @if (!isProgress()) {
            @if (editingStatus()) {
              <div class="section__actions">
                <button class="btn-sm" (click)="cancelEdit('status')">Cancel</button>
                <button class="btn-sm btn-sm--primary" (click)="saveFile('status.md', statusDraft())">Save</button>
              </div>
            } @else {
              <button class="btn-sm" (click)="startEdit('status')">✏️ Edit</button>
            }
          }
        </div>
        @if (editingStatus()) {
          <textarea class="section__editor" [(ngModel)]="statusDraftValue" rows="10"></textarea>
        } @else {
          <pre class="section__body">{{ detail().statusMarkdown || '(empty)' }}</pre>
        }
      </section>

      @if (detail().log.length > 0) {
        <section class="section">
          <h3 class="section__title">Protocol</h3>
          <div class="log">
            @for (entry of detail().log; track entry.timestamp) {
              <div class="log__row">
                <span class="log__time">{{ formatTime(entry.timestamp) }}</span>
                <span class="log__event">{{ entry.event }}</span>
                @if (entry.detail) {
                  <span class="log__detail">{{ entry.detail }}</span>
                }
              </div>
            }
          </div>
        </section>
      }
    </div>
  `,
  styles: [`
    .detail { padding: 0; }

    .detail__header {
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 8px;
    }
    .detail__back {
      background: rgba(255,255,255,0.06);
      border: none;
      color: #94a3b8;
      width: 32px; height: 32px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 16px;
      display: grid; place-items: center;
    }
    .detail__back:hover { background: rgba(255,255,255,0.1); }
    .detail__title { margin: 0; font-size: 18px; color: #e2e8f0; flex: 1; }
    .detail__state {
      font-size: 11px;
      text-transform: uppercase;
      padding: 4px 10px;
      border-radius: 6px;
      font-weight: 600;
      letter-spacing: 0.4px;
    }
    .state--1-preparation { background: rgba(139,92,246,0.15); color: #8b5cf6; }
    .state--2-ready { background: rgba(6,182,212,0.15); color: #06b6d4; }
    .state--3-progress { background: rgba(59,130,246,0.15); color: #3b82f6; }
    .state--4-review { background: rgba(245,158,11,0.15); color: #f59e0b; }
    .state--5-completed { background: rgba(16,185,129,0.15); color: #10b981; }

    .detail__meta {
      display: flex;
      gap: 16px;
      font-size: 12px;
      color: #64748b;
      margin-bottom: 24px;
      padding-bottom: 16px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      align-items: center;
    }
    .detail__project-select {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.1);
      color: #e2e8f0;
      padding: 3px 8px;
      border-radius: 6px;
      font-size: 12px;
      cursor: pointer;
    }
    .detail__project-select:hover { border-color: rgba(255,255,255,0.2); }
    .detail__project-select:focus { outline: none; border-color: #6366f1; }

    .section { margin-bottom: 24px; }
    .section__header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 8px;
    }
    .section__title {
      font-size: 12px;
      color: #64748b;
      text-transform: uppercase;
      letter-spacing: 0.5px;
      margin: 0;
    }
    .section__body {
      background: rgba(0,0,0,0.2);
      padding: 16px;
      border-radius: 8px;
      white-space: pre-wrap;
      word-break: break-word;
      font-size: 13px;
      line-height: 1.6;
      color: #cbd5e1;
      border: 1px solid rgba(255,255,255,0.04);
      margin: 0;
    }

    .log { display: flex; flex-direction: column; gap: 2px; }
    .log__row {
      display: flex;
      gap: 12px;
      align-items: baseline;
      padding: 8px 12px;
      background: rgba(0,0,0,0.15);
      border-radius: 6px;
      font-size: 13px;
    }
    .log__time { font-size: 11px; color: #64748b; min-width: 70px; font-variant-numeric: tabular-nums; }
    .log__event { color: #e2e8f0; }
    .log__detail { color: #94a3b8; font-size: 12px; }

    .btn-sm {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      padding: 4px 10px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 12px;
    }
    .btn-sm:hover { background: rgba(255,255,255,0.1); }
    .btn-sm--primary { background: #6366f1; border-color: #6366f1; color: white; }
    .btn-sm--primary:hover { background: #5558e6; }
    .section__actions { display: flex; gap: 6px; }
    .section__editor {
      width: 100%;
      background: rgba(0,0,0,0.3);
      border: 1px solid rgba(99,102,241,0.4);
      color: #e2e8f0;
      padding: 16px;
      border-radius: 8px;
      font-family: 'Consolas', monospace;
      font-size: 13px;
      line-height: 1.6;
      resize: vertical;
      box-sizing: border-box;
    }
    .section__editor:focus { outline: none; border-color: #6366f1; }
  `]
})
export class JobDetailComponent {
  readonly detail = input.required<JobDetail>();
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly back = output<void>();
  readonly fileSaved = output<void>();
  readonly projectChanged = output<void>();

  readonly editingPrompt = signal(false);
  readonly editingStatus = signal(false);
  readonly promptDraft = signal('');
  readonly statusDraft = signal('');

  promptDraftValue = '';
  statusDraftValue = '';

  constructor(private jobService: JobService) {}

  isProgress(): boolean {
    return this.detail().info.state === '3-progress';
  }

  startEdit(which: 'prompt' | 'status') {
    if (which === 'prompt') {
      this.promptDraftValue = this.detail().promptMarkdown ?? '';
      this.promptDraft.set(this.promptDraftValue);
      this.editingPrompt.set(true);
    } else {
      this.statusDraftValue = this.detail().statusMarkdown ?? '';
      this.statusDraft.set(this.statusDraftValue);
      this.editingStatus.set(true);
    }
  }

  cancelEdit(which: 'prompt' | 'status') {
    if (which === 'prompt') this.editingPrompt.set(false);
    else this.editingStatus.set(false);
  }

  saveFile(fileName: string, _content: string) {
    const content = fileName === 'prompt.md' ? this.promptDraftValue : this.statusDraftValue;
    this.jobService.updateJobFile(this.detail().info.id, fileName, content).subscribe({
      next: () => {
        if (fileName === 'prompt.md') this.editingPrompt.set(false);
        else this.editingStatus.set(false);
        this.fileSaved.emit();
      }
    });
  }

  stateLabel(state: string): string {
    return state.replace(/^\d+-/, '');
  }

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString();
  }

  onProjectChange(targetWatchPath: string) {
    if (targetWatchPath === this.detail().info.watchPath) return;
    this.jobService.changeProject(this.detail().info.id, targetWatchPath).subscribe({
      next: () => this.projectChanged.emit(),
      error: () => {} // select will revert on refresh
    });
  }
}
