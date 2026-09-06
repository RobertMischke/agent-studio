import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ModelMigrationStore } from './model-migration.store';

describe('ModelMigrationStore', () => {
  it('finds an explicit-card proposal with normalized model punctuation', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const store = TestBed.inject(ModelMigrationStore);
    const http = TestBed.inject(HttpTestingController);

    store.ensureLoaded();
    http.expectOne('/api/cli/model-migrations').flush({
      catalogVersion: 'te-1',
      migrations: [{
        from: 'claude-haiku-4-5', to: 'claude-sonnet-5', family: 'claude-haiku',
        rule: 'economy-family-change', safeAuto: false, catalogVersion: 'te-1',
        fromCostClass: 'economy', toCostClass: 'standard',
        fromReasoningLadder: ['low'], toReasoningLadder: ['low', 'medium'],
      }],
      configPins: [], workspaces: [],
    });

    expect(store.proposalFor('claude-haiku-4.5')?.to).toBe('claude-sonnet-5');
    expect(store.proposalFor('claude-opus-5')).toBeNull();
    expect(store.proposalForExplicitPin('claude-haiku-4-5', true)?.to).toBe('claude-sonnet-5');
    expect(store.proposalForExplicitPin('claude-haiku-4-5', false)).toBeNull();
  });
});
