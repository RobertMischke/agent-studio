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
    expect(text).toContain('unread');
    expect(text).toContain('1k');

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

  it('filters rows without removing the three context groups', () => {
    const search = fixture.nativeElement.querySelector('[data-testid="chat-switcher-search"]') as HTMLInputElement;
    search.value = 'A-1';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Global');
    expect(fixture.nativeElement.textContent).toContain('Projects');
    expect(fixture.nativeElement.textContent).toContain('Tasks');
  });
});
