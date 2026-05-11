import { describe, it, expect, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { RoleBadgeComponent } from './role-badge.component';

describe('RoleBadgeComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('renders the resolved role label and exposes the description via title', () => {
    const fixture = TestBed.createComponent(RoleBadgeComponent);
    fixture.componentRef.setInput('author', 'claude');
    fixture.componentRef.setInput('kind', 'turn');
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.role-badge') as HTMLElement;
    expect(badge.getAttribute('data-testid')).toBe('role-badge-task-executor');
    expect(badge.getAttribute('title')).toMatch(/Performs the task/);
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
    expect(badge.getAttribute('title')).toBeTruthy();
  });

  it('uses a plain text title attribute (no HTML in the tooltip)', () => {
    const fixture = TestBed.createComponent(RoleBadgeComponent);
    fixture.componentRef.setInput('roleId', 'code-reviewer');
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.role-badge') as HTMLElement;
    const title = badge.getAttribute('title') ?? '';
    expect(title.includes('<')).toBe(false);
    expect(title.includes('>')).toBe(false);
  });
});
