import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatSwitcherRailComponent } from './chat-switcher-rail.component';

describe('ChatSwitcherRailComponent', () => {
  let fixture: ComponentFixture<ChatSwitcherRailComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ChatSwitcherRailComponent] }).compileComponents();
    fixture = TestBed.createComponent(ChatSwitcherRailComponent);
    fixture.componentRef.setInput('projects', ['Alpha']);
    fixture.componentRef.setInput('sessions', [{
      contextKey: 'task:Alpha/A-1', kind: 'task', projectId: 'Alpha', taskKey: 'A-1',
      updatedAt: '2026-07-11T10:00:00Z', model: 'codex', cumulativeInputTokens: 1200,
      cumulativeOutputTokens: 50, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'parked', queuePosition: 0,
    }]);
    fixture.detectChanges();
  });

  it('starts collapsed and expands into global, project and task groups', () => {
    expect(fixture.componentInstance.expanded()).toBe(false);
    fixture.nativeElement.querySelector('[data-testid="chat-switcher-chip"]').click();
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Global');
    expect(text).toContain('Projects');
    expect(text).toContain('Tasks');
    expect(text).toContain('parked');
    expect(text).toContain('1k');
  });

  it('keeps chat selection separate from location navigation', () => {
    const selected = vi.fn();
    const navigated = vi.fn();
    fixture.componentInstance.contextSelected.subscribe(selected);
    fixture.componentInstance.locationRequested.subscribe(navigated);
    fixture.componentInstance.expanded.set(true);
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"] .rail__name').click();
    fixture.nativeElement.querySelector('[data-testid="chat-switcher-navigate-task:Alpha/A-1"]').click();
    expect(selected).toHaveBeenCalledWith('task:Alpha/A-1');
    expect(navigated).toHaveBeenCalledWith('task:Alpha/A-1');
  });
});
