import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliContractsPanelComponent } from './cli-contracts-panel';
import type { CliCompletionContract } from '../../../../features/cli';

const SAMPLE: CliCompletionContract[] = [
  {
    cliType: 'claude',
    transport: 'stream-json NDJSON',
    sessionStartSignal: 'system frame, subtype=init',
    completionSignal: 'result frame, is_error=false',
    failureSignal: 'result frame, is_error=true',
    usageSource: 'result.usage',
    typed: true,
    notes: 'ClaudeEventAdapter.',
  },
];

describe('CliContractsPanelComponent', () => {
  it('loads contracts from /api/cli/contracts on init', async () => {
    await TestBed.configureTestingModule({
      imports: [CliContractsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(CliContractsPanelComponent);
    fixture.detectChanges();

    const req = http.expectOne((r) => r.url.endsWith('/cli/contracts'));
    req.flush(SAMPLE);
    fixture.detectChanges();

    expect(fixture.componentInstance.contracts().length).toBe(1);
    expect(fixture.componentInstance.contracts()[0].typed).toBe(true);
    const explainer = fixture.nativeElement.querySelector('[data-testid="cli-contracts-explainer"]') as HTMLElement;
    expect(explainer.textContent).toContain('read-only registry');
    expect(explainer.textContent).toContain('GET /api/cli/contracts');
    expect(explainer.textContent).toContain('not configuration');
    http.verify();
  });
});
