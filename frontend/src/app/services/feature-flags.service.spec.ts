import { TestBed } from '@angular/core/testing';
import { FeatureFlagsService } from './feature-flags.service';

const KEY_VS_CODE_LAYOUT = 'atp.flag.vsCodeLayout';
const KEY_NEXT_GEN_CHAT = 'atp.flag.nextGenChat';

describe('FeatureFlagsService — vsCodeLayout default ON', () => {
  beforeEach(() => {
    localStorage.removeItem(KEY_VS_CODE_LAYOUT);
    localStorage.removeItem(KEY_NEXT_GEN_CHAT);
  });

  function build(): FeatureFlagsService {
    TestBed.configureTestingModule({ providers: [FeatureFlagsService] });
    return TestBed.inject(FeatureFlagsService);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(KEY_VS_CODE_LAYOUT);
    localStorage.removeItem(KEY_NEXT_GEN_CHAT);
  });

  it('reads vsCodeLayout as true when the key is absent (new default)', () => {
    const svc = build();
    expect(svc.vsCodeLayout()).toBe(true);
  });

  it('reads vsCodeLayout as true when the key is "1"', () => {
    localStorage.setItem(KEY_VS_CODE_LAYOUT, '1');
    const svc = build();
    expect(svc.vsCodeLayout()).toBe(true);
  });

  it('reads vsCodeLayout as false when the key is explicit "0"', () => {
    localStorage.setItem(KEY_VS_CODE_LAYOUT, '0');
    const svc = build();
    expect(svc.vsCodeLayout()).toBe(false);
  });

  it('setVsCodeLayout(false) persists "0" so the next reload stays off', () => {
    const svc = build();
    svc.setVsCodeLayout(false);
    expect(localStorage.getItem(KEY_VS_CODE_LAYOUT)).toBe('0');
    expect(svc.vsCodeLayout()).toBe(false);
  });

  it('setVsCodeLayout(true) persists "1"', () => {
    localStorage.setItem(KEY_VS_CODE_LAYOUT, '0');
    const svc = build();
    expect(svc.vsCodeLayout()).toBe(false);
    svc.setVsCodeLayout(true);
    expect(localStorage.getItem(KEY_VS_CODE_LAYOUT)).toBe('1');
    expect(svc.vsCodeLayout()).toBe(true);
  });

  it('other flags (nextGenChat) keep their default-off semantics', () => {
    const svc = build();
    expect(svc.nextGenChat()).toBe(false);
  });
});
