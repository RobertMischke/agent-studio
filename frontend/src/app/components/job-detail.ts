import { Component, input, output, signal, effect, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobDetail, WatchPathEntry, CliOutputLine, CliSettings } from '../models/job.model';
import { JobService } from '../services/job.service';
import { CliConsoleComponent } from './cli-console';

@Component({
  selector: 'app-job-detail',
  standalone: true,
  imports: [FormsModule, CliConsoleComponent],
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

      @if (canStartJob() || isRunning()) {
        <div class="execution-bar">
          @if (isRunning()) {
            <div class="execution-bar__status">
              <span class="execution-bar__pulse"></span>
              <span class="execution-bar__text">Running since {{ elapsedTime() }}</span>
            </div>
            <button class="btn-exec btn-exec--stop" (click)="stopJob()">⏹ Stop</button>
          } @else {
            <button class="btn-exec btn-exec--start" (click)="startJob()" [disabled]="starting()">
              {{ starting() ? '⏳ Starting...' : '▶ Start CLI' }}
            </button>
          }
        </div>
      }

      @if (errorMsg(); as err) {
        <div class="detail__error">
          <span class="detail__error-icon">⚠️</span>
          <span class="detail__error-text">{{ err }}</span>
          @if (isCliError()) {
            <button class="detail__error-action" (click)="openCliConfig()">🔧 Configure</button>
          }
          <button class="detail__error-close" (click)="dismissError()">✕</button>
        </div>
      }

      @if (showCliConfig()) {
        <div class="cli-config">
          <div class="cli-config__header">
            <span class="cli-config__title">🔧 CLI Configuration</span>
            <button class="detail__error-close" (click)="showCliConfig.set(false)">✕</button>
          </div>
          @if (cliStatus()) {
            <div class="cli-config__status" [class.cli-config__status--ok]="cliStatus()!.available"
                 [class.cli-config__status--err]="!cliStatus()!.available">
              @if (cliStatus()!.available) {
                ✅ {{ cliStatus()!.version }} — {{ cliStatus()!.path }}
              } @else {
                ❌ Not found at: {{ cliStatus()!.path }}
              }
            </div>
          }
          <div class="cli-config__row">
            <input class="cli-config__input" type="text"
                   [value]="cliPathDraft()"
                   (input)="cliPathDraft.set($any($event.target).value)"
                   placeholder="z.B. copilot, C:\\Program Files\\GitHub\\copilot.exe" />
            <button class="btn-sm" (click)="testCliPath()" [disabled]="cliTesting()">
              {{ cliTesting() ? '⏳' : '🧪 Test' }}
            </button>
            <button class="btn-sm btn-sm--primary" (click)="saveCliPath()" [disabled]="cliTesting()">💾 Save</button>
          </div>
          @if (cliTestResult(); as result) {
            <div class="cli-config__status" [class.cli-config__status--ok]="result.available"
                 [class.cli-config__status--err]="!result.available">
              @if (result.available) {
                ✅ Found: {{ result.version }}
              } @else {
                ❌ {{ result.version || 'Not found at this path' }}
              }
            </div>
          }
          <div class="cli-config__separator"></div>
          <div class="cli-config__row">
            <input class="cli-config__input" [type]="showToken() ? 'text' : 'password'"
                   [value]="tokenDraft()"
                   (input)="tokenDraft.set($any($event.target).value)"
                   placeholder="GitHub Token (PAT or OAuth)" />
            <button class="btn-sm" (click)="showToken.set(!showToken())">
              {{ showToken() ? '🙈' : '👁' }}
            </button>
            <button class="btn-sm btn-sm--primary" (click)="saveToken()" [disabled]="tokenSaving()">
              {{ tokenSaving() ? '⏳' : '💾 Save Token' }}
            </button>
          </div>
          @if (cliStatus()) {
            <div class="cli-config__status" [class.cli-config__status--ok]="cliStatus()!.hasToken"
                 [class.cli-config__status--err]="!cliStatus()!.hasToken">
              {{ cliStatus()!.hasToken ? '🔑 Token configured' : '⚠️ No token — CLI may fail to authenticate' }}
            </div>
          }
        </div>
      }

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

      @if (cliOutput().length > 0 || isRunning()) {
        <section class="section">
          <app-cli-console [lines]="cliOutput()" />
        </section>
      }

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

    .execution-bar {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 10px 14px;
      background: rgba(0,0,0,0.2);
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 8px;
      margin-bottom: 20px;
    }
    .execution-bar__status {
      display: flex;
      align-items: center;
      gap: 10px;
      font-size: 13px;
      color: #94a3b8;
    }
    .execution-bar__pulse {
      width: 8px; height: 8px;
      border-radius: 50%;
      background: #3b82f6;
      animation: pulse 1.5s infinite;
    }
    @keyframes pulse {
      0%, 100% { opacity: 1; box-shadow: 0 0 0 0 rgba(59,130,246,0.4); }
      50% { opacity: 0.7; box-shadow: 0 0 0 6px rgba(59,130,246,0); }
    }
    .execution-bar__text { font-variant-numeric: tabular-nums; }
    .btn-exec {
      border: none;
      padding: 6px 16px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 13px;
      font-weight: 600;
    }
    .btn-exec--start {
      background: rgba(34,197,94,0.15);
      color: #4ade80;
      border: 1px solid rgba(34,197,94,0.3);
    }
    .btn-exec--start:hover { background: rgba(34,197,94,0.25); }
    .btn-exec--stop {
      background: rgba(239,68,68,0.15);
      color: #f87171;
      border: 1px solid rgba(239,68,68,0.3);
    }
    .btn-exec--stop:hover { background: rgba(239,68,68,0.25); }
    .btn-exec:disabled { opacity: 0.5; cursor: wait; }

    .detail__error {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px 14px;
      background: rgba(239,68,68,0.1);
      border: 1px solid rgba(239,68,68,0.25);
      border-radius: 8px;
      margin-bottom: 20px;
      animation: fadeIn 0.2s ease;
    }
    .detail__error-icon { font-size: 14px; flex-shrink: 0; }
    .detail__error-text { font-size: 13px; color: #fca5a5; flex: 1; }
    .detail__error-close {
      background: none;
      border: none;
      color: #f87171;
      cursor: pointer;
      font-size: 14px;
      padding: 2px 6px;
      border-radius: 4px;
      flex-shrink: 0;
    }
    .detail__error-close:hover { background: rgba(239,68,68,0.15); }
    .detail__error-action {
      background: rgba(99,102,241,0.15);
      border: 1px solid rgba(99,102,241,0.3);
      color: #a5b4fc;
      cursor: pointer;
      font-size: 12px;
      padding: 3px 10px;
      border-radius: 4px;
      font-weight: 600;
      flex-shrink: 0;
      white-space: nowrap;
    }
    .detail__error-action:hover { background: rgba(99,102,241,0.25); }

    .cli-config {
      background: rgba(99,102,241,0.06);
      border: 1px solid rgba(99,102,241,0.15);
      border-radius: 8px;
      padding: 14px;
      margin-bottom: 20px;
      animation: fadeIn 0.2s ease;
    }
    .cli-config__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 10px;
    }
    .cli-config__title {
      font-size: 13px;
      font-weight: 600;
      color: #c4b5fd;
    }
    .cli-config__row {
      display: flex;
      gap: 8px;
      align-items: center;
    }
    .cli-config__input {
      flex: 1;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #e2e8f0;
      padding: 6px 10px;
      border-radius: 6px;
      font-size: 13px;
      font-family: 'Cascadia Code', 'Fira Code', monospace;
    }
    .cli-config__input:focus {
      outline: none;
      border-color: #6366f1;
    }
    .cli-config__status {
      font-size: 12px;
      padding: 6px 10px;
      border-radius: 6px;
      margin-top: 8px;
    }
    .cli-config__status--ok {
      background: rgba(34,197,94,0.1);
      color: #4ade80;
    }
    .cli-config__status--err {
      background: rgba(239,68,68,0.1);
      color: #fca5a5;
    }
    .cli-config__separator {
      border-top: 1px solid rgba(255,255,255,0.08);
      margin: 8px 0 4px;
    }
    @keyframes fadeIn {
      from { opacity: 0; transform: translateY(-4px); }
      to { opacity: 1; transform: translateY(0); }
    }
  `]
})
export class JobDetailComponent implements OnDestroy {
  readonly detail = input.required<JobDetail>();
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly back = output<void>();
  readonly fileSaved = output<void>();
  readonly projectChanged = output<string>();

  readonly editingPrompt = signal(false);
  readonly editingStatus = signal(false);
  readonly promptDraft = signal('');
  readonly statusDraft = signal('');
  readonly cliOutput = signal<CliOutputLine[]>([]);
  readonly isRunning = signal(false);
  readonly startedAt = signal<Date | null>(null);
  readonly elapsedTime = signal('');
  readonly errorMsg = signal<string | null>(null);
  readonly starting = signal(false);
  readonly showCliConfig = signal(false);
  readonly cliStatus = signal<CliSettings | null>(null);
  readonly cliPathDraft = signal('');
  readonly cliTestResult = signal<CliSettings | null>(null);
  readonly cliTesting = signal(false);
  readonly tokenDraft = signal('');
  readonly showToken = signal(false);
  readonly tokenSaving = signal(false);

  promptDraftValue = '';
  statusDraftValue = '';
  private elapsedTimer: ReturnType<typeof setInterval> | null = null;

  constructor(private jobService: JobService) {}

  private detailEffect = effect(() => {
    const d = this.detail();
    this.errorMsg.set(null);
    if (d.info.state === '3-progress') {
      // Try to load existing output
      this.jobService.getJobOutput(d.info.id, d.info.watchPath).subscribe({
        next: (output) => {
          if (output.length > 0) {
            this.cliOutput.set(output);
            this.isRunning.set(true);
            this.startedAt.set(new Date());
            this.startElapsedTimer();
          }
        },
        error: (err) => {
          if (err.status !== 0) return; // silent for 404 etc
          this.showError(err);
        }
      });
    }
  });

  ngOnDestroy() {
    this.detailEffect.destroy();
    if (this.elapsedTimer) clearInterval(this.elapsedTimer);
  }

  canStartJob(): boolean {
    const state = this.detail().info.state;
    return (state === '2-ready' || state === '3-progress') && !this.isRunning();
  }

  startJob(): void {
    this.errorMsg.set(null);
    this.starting.set(true);
    this.jobService.startJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: (exec) => {
        this.starting.set(false);
        this.isRunning.set(true);
        this.startedAt.set(new Date(exec.startedAt));
        this.cliOutput.set([]);
        this.startElapsedTimer();
        this.pollOutput();
      },
      error: (err) => {
        this.starting.set(false);
        this.showError(err);
      }
    });
  }

  stopJob(): void {
    this.errorMsg.set(null);
    this.jobService.stopJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: () => {
        this.isRunning.set(false);
        if (this.elapsedTimer) clearInterval(this.elapsedTimer);
      },
      error: (err) => this.showError(err)
    });
  }

  private showError(err: any): void {
    if (err.status === 0) {
      this.errorMsg.set('Backend not reachable — is the API running on localhost:5030?');
    } else if (err.error?.error) {
      this.errorMsg.set(err.error.error);
    } else if (typeof err.error === 'string') {
      this.errorMsg.set(err.error);
    } else {
      this.errorMsg.set(`Request failed (${err.status || 'unknown'}): ${err.statusText || err.message || 'Unknown error'}`);
    }
  }

  private startElapsedTimer(): void {
    if (this.elapsedTimer) clearInterval(this.elapsedTimer);
    this.updateElapsed();
    this.elapsedTimer = setInterval(() => this.updateElapsed(), 1000);
  }

  private updateElapsed(): void {
    const start = this.startedAt();
    if (!start) { this.elapsedTime.set('0s'); return; }
    const secs = Math.floor((Date.now() - start.getTime()) / 1000);
    if (secs < 60) this.elapsedTime.set(`${secs}s`);
    else if (secs < 3600) this.elapsedTime.set(`${Math.floor(secs / 60)}m ${secs % 60}s`);
    else this.elapsedTime.set(`${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m`);
  }

  private pollOutput(): void {
    // Poll every 2 seconds while running (SignalR will augment this)
    const poll = () => {
      if (!this.isRunning()) return;
      this.jobService.getJobOutput(this.detail().info.id, this.detail().info.watchPath).subscribe({
        next: (output) => {
          this.cliOutput.set(output);
          setTimeout(poll, 2000);
        },
        error: () => setTimeout(poll, 5000)
      });
    };
    setTimeout(poll, 1000);
  }

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
    this.jobService.updateJobFile(this.detail().info.id, fileName, content, this.detail().info.watchPath).subscribe({
      next: () => {
        if (fileName === 'prompt.md') this.editingPrompt.set(false);
        else this.editingStatus.set(false);
        this.fileSaved.emit();
      },
      error: (err) => this.showError(err)
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

  isCliError(): boolean {
    const msg = this.errorMsg();
    return !!msg && (msg.includes('CLI') || msg.includes('copilot') || msg.includes('Copilot') || msg.includes('authenticat'));
  }

  openCliConfig(): void {
    this.showCliConfig.set(true);
    this.cliTestResult.set(null);
    this.jobService.getCliSettings().subscribe({
      next: (settings) => {
        this.cliStatus.set(settings);
        this.cliPathDraft.set(settings.path);
      },
      error: (err) => this.showError(err)
    });
  }

  dismissError(): void {
    this.errorMsg.set(null);
    this.showCliConfig.set(false);
  }

  testCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.cliTestResult.set(null);
    this.jobService.testCliPath(path).subscribe({
      next: (result) => {
        this.cliTestResult.set(result);
        this.cliTesting.set(false);
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.jobService.setCliPath(path).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.cliTestResult.set(null);
        this.cliTesting.set(false);
        if (result.available) {
          this.errorMsg.set(null);
          this.showCliConfig.set(false);
        }
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveToken(): void {
    const token = this.tokenDraft().trim();
    if (!token) return;
    this.tokenSaving.set(true);
    this.jobService.setGitHubToken(token).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.tokenSaving.set(false);
        this.tokenDraft.set('');
        if (result.hasToken && result.available) {
          this.errorMsg.set(null);
        }
      },
      error: (err) => {
        this.tokenSaving.set(false);
        this.showError(err);
      }
    });
  }

  onProjectChange(targetWatchPath: string) {
    if (targetWatchPath === this.detail().info.watchPath) return;
    this.jobService.changeProject(this.detail().info.id, targetWatchPath, this.detail().info.watchPath).subscribe({
      next: () => this.projectChanged.emit(targetWatchPath),
      error: (err) => this.showError(err)
    });
  }
}
