import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import type { TaskIntegrationStatus, IntegrationStatusValue } from '../../features/git';
import { IntegrationStatusBadgeComponent } from './integration-status-badge.component';

function integration(
  status: IntegrationStatusValue,
  overrides: Partial<TaskIntegrationStatus> = {},
): TaskIntegrationStatus {
  return {
    status,
    sha: status === 'integrated' ? 'abc1234' : null,
    integrationBranch: 'develop',
    detail: null,
    ...overrides,
  };
}

describe('IntegrationStatusBadgeComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [IntegrationStatusBadgeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  function render(value: TaskIntegrationStatus | null) {
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput('integration', value);
    fixture.detectChanges();
    return fixture;
  }

  it('renders integrated as green "merged @sha"', () => {
    const fixture = render(integration('integrated', { sha: 'deadbee' }));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('merged @deadbee');
    expect(badge.dataset['kind']).toBe('integrated');
    expect(badge.classList.contains('integration-badge--acute')).toBe(false);
  });

  it('renders pending as amber "NICHT integriert" and flags acute', () => {
    const fixture = render(integration('pending'));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('NICHT integriert');
    expect(badge.dataset['kind']).toBe('pending');
    expect(badge.classList.contains('integration-badge--acute')).toBe(true);
  });

  it('renders conflict-skipped as a red conflict badge', () => {
    const fixture = render(integration('conflict-skipped', { detail: 'Conflicted: a.txt' }));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('Konflikt');
    expect(badge.dataset['kind']).toBe('conflict');
    expect(badge.classList.contains('integration-badge--acute')).toBe(true);
    expect(fixture.componentInstance.tooltip()).toContain('Conflicted: a.txt');
  });

  it('renders no-branch as grey "kein Branch"', () => {
    const fixture = render(integration('no-branch'));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('kein Branch');
    expect(badge.dataset['kind']).toBe('no-branch');
    expect(badge.classList.contains('integration-badge--acute')).toBe(false);
  });

  it('hides when there is no integration verdict', () => {
    const fixture = render(null);
    expect(fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]')).toBeNull();
  });

  it('honours a custom integration branch in the label and tooltip', () => {
    const fixture = render(integration('pending', { integrationBranch: 'trunk' }));
    expect(fixture.componentInstance.tooltip()).toContain('NOT integrated into trunk');
    expect(fixture.componentInstance.ariaLabel()).toContain('Not integrated into trunk');
  });
});
