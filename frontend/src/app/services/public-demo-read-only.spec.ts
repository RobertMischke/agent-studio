import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { AuthSessionState, AuthStatus } from './auth.service';
import { UpdateClientService } from './update.service';

/**
 * AGT-W34 slice S4: the public read-only demo must reach the UI as one signal,
 * so every control already threaded on `mutationsBlocked` disables itself
 * without each call site learning about the demo profile.
 */
describe('public read-only demo state', () => {
  const status = (profile: AuthStatus['profile']): AuthStatus => ({
    profile,
    bootstrapRequired: false,
    authenticated: profile === 'networked',
    user: null,
  });

  let auth: AuthSessionState;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthSessionState);
  });

  it('recognises the public demo profile', () => {
    expect(auth.publicDemo()).toBe(false);
    auth.status.set(status('public-demo'));
    expect(auth.publicDemo()).toBe(true);
  });

  it('admits a visitor to the studio without a session', () => {
    auth.status.set(status('public-demo'));
    expect(auth.studioAllowed()).toBe(true);
    // A visitor is not an authenticated operator: no sign-out affordance.
    expect(auth.networkedAuthenticated()).toBe(false);
  });

  it('blocks mutating UI controls for the whole demo session', () => {
    const updates = TestBed.inject(UpdateClientService);
    expect(updates.mutationsBlocked()).toBe(false);

    auth.status.set(status('public-demo'));
    expect(updates.mutationsBlocked()).toBe(true);
  });

  it('leaves the networked profile controls alone', () => {
    const updates = TestBed.inject(UpdateClientService);
    auth.status.set(status('networked'));
    expect(updates.mutationsBlocked()).toBe(false);
  });
});
