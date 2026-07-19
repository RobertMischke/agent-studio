import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectBasicsFormComponent } from './project-basics-form.component';

describe('ProjectBasicsFormComponent', () => {
  it('derives a short code while creating and stops after a manual edit', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectBasicsFormComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const component = TestBed.createComponent(ProjectBasicsFormComponent).componentInstance;

    component.onNameChange('Quality Studio');
    expect(component.shortCode()).toBe('QS');
    component.onCodeChange('custom');
    component.onNameChange('Renamed Studio');
    expect(component.shortCode()).toBe('CUSTOM');
  });
});
