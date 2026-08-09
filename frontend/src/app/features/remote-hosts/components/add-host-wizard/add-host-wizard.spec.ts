import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AddHostWizardComponent } from './add-host-wizard';

describe('AddHostWizardComponent', () => {
  it('provisions provider auth through the protected endpoint and clears the secret', async () => {
    await TestBed.configureTestingModule({
      imports: [AddHostWizardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AddHostWizardComponent);
    const component = fixture.componentInstance;
    const secret = 'sk-ant-oat01-provider-auth-fixture';
    component.name.set('agent-runner-02');
    component.address.set('ssh://agent@runner-02');
    component.providerAuthSecret.set(secret);
    component.provisionProviderAuth();

    const request = TestBed.inject(HttpTestingController).expectOne(
      '/api/v1/management/remote-hosts/provider-auth',
    );
    expect(request.request.body).toEqual({
      sshTarget: 'agent@runner-02',
      runnerId: 'agent-runner-02',
      environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
      secret,
    });
    request.flush({
      provider: 'claude',
      environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
      host: 'agent@runner-02',
      state: 'installed-awaiting-runner',
      detail: 'The protected EnvironmentFile was installed.',
      requestedAt: '2026-08-04T12:00:00Z',
      restartedServices: [],
      processEnvironmentVerified: false,
    });
    fixture.detectChanges();

    expect(component.providerAuthSecret()).toBe('');
    expect(component.providerAuthPhase()).toBe('waiting');
    expect(component.claudeAuthed()).toBe(false);
    expect(fixture.nativeElement.textContent).not.toContain(secret);
  });
});
