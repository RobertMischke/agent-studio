import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PipelineStepToggleComponent } from './pipeline-step-toggle.component';

describe('PipelineStepToggleComponent', () => {
  let fixture: ComponentFixture<PipelineStepToggleComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineStepToggleComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    fixture = TestBed.createComponent(PipelineStepToggleComponent);
    fixture.componentRef.setInput('stepId', 'aspect-code-quality');
    fixture.componentRef.setInput('stepName', 'Code quality');
    fixture.componentRef.setInput('enabled', true);
    fixture.componentRef.setInput('canDisable', true);
    fixture.detectChanges();
  });

  it('emits a state change without allowing the click to reach the row summary', () => {
    const host = fixture.nativeElement as HTMLElement;
    const input = host.querySelector<HTMLInputElement>(
      '[data-testid="pipeline-step-enabled-aspect-code-quality"]',
    );
    const parentClick = vi.fn();
    const enabledChange = vi.fn();
    host.parentElement?.addEventListener('click', parentClick);
    fixture.componentInstance.enabledChange.subscribe(enabledChange);

    input?.click();

    expect(enabledChange).toHaveBeenCalledWith(false);
    expect(parentClick).not.toHaveBeenCalled();
  });

  it('renders fixed catalogue steps as checked, disabled controls', async () => {
    fixture.componentRef.setInput('stepId', 'core-agent-run');
    fixture.componentRef.setInput('stepName', 'Agent execution');
    fixture.componentRef.setInput('canDisable', false);
    await fixture.whenStable();
    fixture.detectChanges();

    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '[data-testid="pipeline-step-enabled-core-agent-run"]',
    );

    expect(input?.checked).toBe(true);
    expect(input?.disabled).toBe(true);
  });
});
