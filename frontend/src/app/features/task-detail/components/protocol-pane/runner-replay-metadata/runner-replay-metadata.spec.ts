import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { RunnerReplayMetadataComponent } from './runner-replay-metadata';

describe('RunnerReplayMetadataComponent', () => {
  it('renders lifecycle facts as metadata and separates implementation from pipeline state', async () => {
    await TestBed.configureTestingModule({
      imports: [RunnerReplayMetadataComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunnerReplayMetadataComponent);
    fixture.componentRef.setInput('events', [{
      id: 'turn-2149', kind: 'turn.completed', timestamp: '2026-07-22T10:00:00Z',
      sessionId: 'session-2149', model: 'gpt-5.4', thinkingLevel: 'high', durationMs: 412_000,
      inputTokens: 74_192, outputTokens: 8_331,
      implementationStatus: 'completed', pipelineStatus: 'post-processing',
    }]);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Turn completed');
    expect(text).toContain('session-2149');
    expect(text).toContain('6m 52s');
    expect(text).not.toContain('Turn completed (tokens:');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="runner-replay-implementation"]')?.textContent).toContain('completed');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="runner-replay-pipeline"]')?.textContent).toContain('post-processing');
  });

  it('leaves a real run unlabelled', async () => {
    const element = await render([{
      id: 'turn-1', kind: 'turn.completed', timestamp: '2026-07-22T10:00:00Z', durationMs: 1_000,
    }]);

    expect(element.querySelector('[data-testid="runner-replay-simulated"]')).toBeNull();
    expect(element.querySelector('[data-testid="runner-replay-event-simulated"]')).toBeNull();
  });

  it('labels a replayed row Simulated on the row and on the section', async () => {
    const element = await render([{
      id: 'demo-replay:1:3', kind: 'turn.completed', timestamp: '2026-08-17T09:00:00Z',
      durationMs: 18_000, simulated: true,
    }]);

    expect(element.querySelector('[data-testid="runner-replay-simulated"]')?.textContent).toContain('Simulated');
    expect(element.querySelector('[data-testid="runner-replay-event-simulated"]')?.textContent).toContain('Simulated');
    expect(element.querySelector('[data-testid="runner-replay-demo-replay:1:3"]')?.getAttribute('data-simulated')).toBe('true');
  });

  it('labels the section when only some rows are replayed', async () => {
    const element = await render([
      { id: 'turn-1', kind: 'turn.completed', timestamp: '2026-08-17T09:00:00Z', durationMs: 1_000 },
      { id: 'demo-replay:1:3', kind: 'turn.completed', timestamp: '2026-08-17T09:01:00Z', durationMs: 1_000, simulated: true },
    ]);

    expect(element.querySelector('[data-testid="runner-replay-simulated"]')).not.toBeNull();
    expect(element.querySelectorAll('[data-testid="runner-replay-event-simulated"]')).toHaveLength(1);
  });
});

async function render(events: unknown[]): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [RunnerReplayMetadataComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(RunnerReplayMetadataComponent);
  fixture.componentRef.setInput('events', events);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}
