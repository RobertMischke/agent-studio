import { TestBed } from '@angular/core/testing';
import { ProjectBuildProfileNoticeComponent } from './project-build-profile-notice.component';

describe('ProjectBuildProfileNoticeComponent', () => {
  it.each([
    [null, 'No BuildProfile is declared and no verify commands can be derived.'],
    [{ status: 'declared' }, 'The declared BuildProfile and repository layout provide no verify commands.'],
  ])('explains the empty verify plan for profile %j', async (profile, expectedCopy) => {
    await TestBed.configureTestingModule({ imports: [ProjectBuildProfileNoticeComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectBuildProfileNoticeComponent);
    fixture.componentRef.setInput('summary', {
      profile,
      gateApplicable: false,
      verifyPlan: { source: 'none', commands: [] },
    });
    fixture.detectChanges();

    const notice = fixture.nativeElement.querySelector(
      '[data-testid="project-settings-no-verify-commands"]',
    ) as HTMLElement;
    expect(notice.textContent).toContain(expectedCopy);
    expect(notice.textContent).toContain('Not applicable');
    expect(notice.querySelector('a')?.href).toContain('contributor-setup.md#onboarding-checklist');
  });
});
