import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ConversationSessionCardComponent } from './conversation-session-card.component';
import { parseRateLimit, type SessionCardData } from '../conversation-session-meta';

function makeFixture(data: SessionCardData) {
  const fixture = TestBed.createComponent(ConversationSessionCardComponent);
  fixture.componentRef.setInput('data', data);
  fixture.detectChanges();
  return fixture;
}

describe('ConversationSessionCardComponent', () => {
  it('renders the short session id with the full id as a tooltip target', async () => {
    const fixture = await makeFixture({
      sessionIdShort: 'a1b2c3',
      sessionIdFull: 'a1b2c3d4-e5f6-7890-abcd-ef0123456789',
      initAt: '2026-05-05T12:00:00.000Z',
    });
    const el: HTMLElement = fixture.nativeElement;

    const id = el.querySelector('[data-testid="conversation-session-card-id"]');
    expect(id?.textContent).toContain('a1b2c3');
    expect(el.querySelector('[data-testid="conversation-session-card"]')).toBeTruthy();
  });

  it('renders the rate-limit window label, status and a reset clock', async () => {
    const rateLimit = parseRateLimit(
      '● Rate limit · five-hour · allowed · reset in 4.4 h ' +
        '[window=five_hour status=allowed resetsAt=1777393800]'
    );
    const fixture = await makeFixture({ sessionIdShort: 'a1b2c3', rateLimit });
    const el: HTMLElement = fixture.nativeElement;

    const pill = el.querySelector('[data-testid="conversation-session-card-ratelimit"]');
    expect(pill).toBeTruthy();
    expect(pill?.textContent).toContain('5h');
    expect(pill?.textContent).toContain('allowed');
    expect(pill?.textContent).toContain('resets');
    expect(pill?.getAttribute('data-status')).toBe('allowed');
  });

  it('omits the rate-limit pill when no rate limit is present', async () => {
    const fixture = await makeFixture({ sessionIdShort: 'a1b2c3' });
    const el: HTMLElement = fixture.nativeElement;
    expect(
      el.querySelector('[data-testid="conversation-session-card-ratelimit"]')
    ).toBeFalsy();
  });
});
