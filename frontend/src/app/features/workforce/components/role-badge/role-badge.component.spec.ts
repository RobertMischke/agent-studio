import { describe, it, expect, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { By } from '@angular/platform-browser';
import { RoleBadgeComponent } from './role-badge.component';
import { TooltipDirective } from '../../../../components/tooltip';

function tooltipContent(fixture: ReturnType<typeof TestBed.createComponent<RoleBadgeComponent>>): string {
  const de = fixture.debugElement.query(By.directive(TooltipDirective));
  return (de.injector.get(TooltipDirective).content as string) ?? '';
}

describe('RoleBadgeComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('renders the resolved role label and exposes the description via [appTooltip]', () => {
    const fixture = TestBed.createComponent(RoleBadgeComponent);
    fixture.componentRef.setInput('author', 'claude');
    fixture.componentRef.setInput('kind', 'turn');
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.role-badge') as HTMLElement;
    expect(badge.getAttribute('data-testid')).toBe('role-badge-task-executor');
    expect(tooltipContent(fixture)).toMatch(/Performs the task/);
    expect(badge.textContent).toContain('Task Executor');
  });

  it('honours an explicit roleId regardless of author/kind/refs', () => {
    const fixture = TestBed.createComponent(RoleBadgeComponent);
    fixture.componentRef.setInput('roleId', 'security-auditor');
    fixture.componentRef.setInput('author', 'claude');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-role-id="security-auditor"]')).toBeTruthy();
  });

  it('renders the agent-generic fallback for an unrecognized author without crashing', () => {
    const fixture = TestBed.createComponent(RoleBadgeComponent);
    fixture.componentRef.setInput('author', 'unknown-cli-from-the-future');
    fixture.componentRef.setInput('kind', 'turn');
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.role-badge') as HTMLElement;
    expect(badge.getAttribute('data-role-id')).toBe('agent-generic');
    expect(tooltipContent(fixture).length).toBeGreaterThan(0);
  });
});
