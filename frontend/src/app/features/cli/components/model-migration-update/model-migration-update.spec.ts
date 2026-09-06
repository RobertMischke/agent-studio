import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { ModelMigrationUpdateComponent } from './model-migration-update';

describe('ModelMigrationUpdateComponent', () => {
  it('offers and applies a Token Economy update for an explicit task pin', async () => {
    await TestBed.configureTestingModule({
      imports: [ModelMigrationUpdateComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ModelMigrationUpdateComponent);
    fixture.componentRef.setInput('model', 'claude-haiku-4-5');
    fixture.componentRef.setInput('modelExplicit', true);
    fixture.componentRef.setInput('taskId', 'AGT-2692');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/cli/model-migrations').flush({
      catalogVersion: 'te-1', configPins: [], workspaces: [],
      migrations: [{
        from: 'claude-haiku-4-5', to: 'claude-sonnet-5', family: 'claude-haiku',
        rule: 'economy-family-change', safeAuto: false, catalogVersion: 'te-1',
        fromCostClass: 'economy', toCostClass: 'standard',
        fromReasoningLadder: ['low'], toReasoningLadder: ['low', 'medium'],
      }],
    });
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button.textContent).toContain('claude-haiku-4-5 to claude-sonnet-5');
    button.click();
    const apply = http.expectOne('/api/tasks/AGT-2692/model');
    expect(apply.request.method).toBe('PUT');
    expect(apply.request.body).toEqual({ model: 'claude-sonnet-5' });
    apply.flush({});
  });
});
