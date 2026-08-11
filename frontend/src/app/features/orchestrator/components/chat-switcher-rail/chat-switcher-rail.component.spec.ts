import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatSwitcherRailComponent } from './chat-switcher-rail.component';

describe('ChatSwitcherRailComponent', () => {
  let fixture: ComponentFixture<ChatSwitcherRailComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ChatSwitcherRailComponent] }).compileComponents();
    fixture = TestBed.createComponent(ChatSwitcherRailComponent);
    fixture.componentRef.setInput('projects', ['Alpha']);
    fixture.componentRef.setInput('activeContextKey', 'task:Alpha/A-1');
    fixture.componentRef.setInput('unreadContextKeys', new Set(['task:Alpha/A-1']));
    fixture.componentRef.setInput('sessions', [{
      contextKey: 'task:Alpha/A-1', kind: 'task', projectId: 'Alpha', taskKey: 'A-1',
      updatedAt: '2026-07-11T10:00:00Z', model: 'codex', cumulativeInputTokens: 1200,
      cumulativeOutputTokens: 50, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'parked', queuePosition: 0,
      summary: 'Investigate the task context lifecycle',
    }]);
    fixture.detectChanges();
  });

  it('always renders the grouped context list with the current row state', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="chat-switcher-chip"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="chat-context-list"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="chat-context-groups"]')).not.toBeNull();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Global');
    expect(text).toContain('Projects');
    expect(text).toContain('Tasks');
    expect(text).toContain('parked');
    expect(text).toContain('new');
    expect(text).toContain('1k');
    expect(text).toContain('Investigate the task context lifecycle');

    const current = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"]');
    expect(current.classList).toContain('context-list__row--active');
  });

  it('keeps chat selection separate from location navigation', () => {
    const selected = vi.fn();
    const navigated = vi.fn();
    fixture.componentInstance.contextSelected.subscribe(selected);
    fixture.componentInstance.locationRequested.subscribe(navigated);

    const row = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"]');
    row.querySelector('.context-list__name').click();
    fixture.nativeElement.querySelector('[data-testid="chat-switcher-navigate-task:Alpha/A-1"]').click();

    expect(selected).toHaveBeenCalledWith('task:Alpha/A-1');
    expect(navigated).toHaveBeenCalledWith('task:Alpha/A-1');
  });

  it('renders exactly one permanent row for each project identity', () => {
    const projectSession = {
      contextKey: 'project:Alpha', kind: 'project' as const, projectId: 'Alpha', taskKey: null,
      updatedAt: '2026-08-10T10:00:00Z', model: null, cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle' as const, queuePosition: 0, summary: 'Permanent project conversation',
    };
    fixture.componentRef.setInput('sessions', [projectSession, { ...projectSession }]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('[data-testid="chat-switcher-row-project:Alpha"]'))
      .toHaveLength(1);
  });

  it('marks a locally active chat even before the session projection catches up', () => {
    fixture.componentRef.setInput('activeChatContextKeys', new Set(['task:Alpha/A-1']));
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"]');
    expect(row.getAttribute('data-runtime-status')).toBe('active');
    expect(row.classList).toContain('context-list__row--working');
    expect(row.textContent).toContain('working');
  });
});
