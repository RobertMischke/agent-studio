import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunExecutionContextComponent } from './run-execution-context.component';

describe('RunExecutionContextComponent', () => {
  it('groups and renders captured execution sources', async () => {
    await TestBed.configureTestingModule({
      imports: [RunExecutionContextComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunExecutionContextComponent);
    fixture.componentRef.setInput('run', {
      index: 2,
      executionContext: {
        source: 'init-frame',
        model: 'gpt-5',
        thinkingLevel: 'high',
        sources: [{ kind: 'memory', label: 'AGENTS.md', path: 'C:/repo/AGENTS.md', exists: true }],
      },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('gpt-5');
    expect(fixture.nativeElement.textContent).toContain('high');
    expect(fixture.nativeElement.textContent).toContain('Memory');
    expect(fixture.nativeElement.querySelector('[data-testid="run-exec-context-2"]')).not.toBeNull();
  });
});
