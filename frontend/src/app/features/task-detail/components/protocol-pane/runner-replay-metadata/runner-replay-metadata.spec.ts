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
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="runner-replay-simulated"]')).toBeNull();
  });

  it('marks replayed demo turns as Simulated on the section and on every event', async () => {
    await TestBed.configureTestingModule({
      imports: [RunnerReplayMetadataComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunnerReplayMetadataComponent);
    fixture.componentRef.setInput('events', [{
      id: 'replay:4:3', kind: 'turn.completed', timestamp: '2026-08-09T08:00:40Z',
      origin: 'simulated', model: 'claude-opus-4-8', outputTokens: 1_200,
    }]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="runner-replay-simulated"]')?.textContent?.trim()).toBe('Simulated');
    expect(host.querySelector('[data-testid="runner-replay-simulated-replay:4:3"]')?.textContent?.trim()).toBe('Simulated');
  });
});
