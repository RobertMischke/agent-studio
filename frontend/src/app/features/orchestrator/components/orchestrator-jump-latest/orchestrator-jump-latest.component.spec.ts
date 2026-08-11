import { TestBed } from '@angular/core/testing';
import { OrchestratorJumpLatestComponent } from './orchestrator-jump-latest.component';

describe('OrchestratorJumpLatestComponent', () => {
  it('mirrors the released CAC state and delegates the jump action', async () => {
    const host = document.createElement('div');
    host.innerHTML = `
      <section data-testid="conversation-view" data-stuck="false">
        <button data-testid="conversation-jump-latest">Jump</button>
      </section>`;
    const libraryButton = host.querySelector<HTMLButtonElement>('button')!;
    let delegated = false;
    libraryButton.addEventListener('click', () => (delegated = true));

    const fixture = TestBed.createComponent(OrchestratorJumpLatestComponent);
    fixture.componentRef.setInput('conversationHost', host);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const jump = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="orchestrator-jump-latest"]',
    );
    expect(jump).toBeTruthy();
    jump!.click();
    expect(delegated).toBe(true);
  });
});
