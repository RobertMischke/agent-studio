import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ProjectBuildVerificationNoteComponent } from './project-build-verification-note.component';

async function build(hasVerifyCommands: boolean, profile: object | null = null) {
  await TestBed.configureTestingModule({
    imports: [ProjectBuildVerificationNoteComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectBuildVerificationNoteComponent);
  fixture.componentRef.setInput('projectName', 'AOW static website');
  fixture.detectChanges();
  TestBed.inject(HttpTestingController)
    .expectOne('/api/projects/AOW%20static%20website/build-profile')
    .flush({ profile, hasVerifyCommands, verifyPlanSource: 'none', verifyCommandCount: 0 });
  fixture.detectChanges();
  return fixture;
}

describe('ProjectBuildVerificationNoteComponent', () => {
  it('shows the BuildProfile convention only when no commands can be derived', async () => {
    const fixture = await build(false);
    const note = fixture.nativeElement.querySelector('[data-testid="project-settings-no-verify-commands"]');

    expect(note?.textContent).toContain('No BuildProfile exists');
    expect(note?.querySelector('a')?.textContent).toContain('BuildProfile convention');
  });

  it('stays absent when verification commands exist', async () => {
    const fixture = await build(true);

    expect(fixture.nativeElement.querySelector('[data-testid="project-settings-no-verify-commands"]')).toBeNull();
  });
});
