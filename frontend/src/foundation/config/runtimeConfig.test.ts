import { describe, it, expect } from 'vitest';
import { loadAppConfig } from './runtimeConfig';

// Issue #126: 実行時 config は window.__APP_CONFIG__ を env より優先し、欠落項目は env で補う。
describe('loadAppConfig', () => {
  it('injected config takes precedence over env defaults', () => {
    const win = {
      __APP_CONFIG__: {
        bffBaseUrl: 'https://edge.example/bff',
        oidc: { authority: 'https://kc.example/realms/kp', clientId: 'spa-web' },
      },
    } as unknown as Window;

    const cfg = loadAppConfig(win);

    expect(cfg.bffBaseUrl).toBe('https://edge.example/bff');
    expect(cfg.oidc.authority).toBe('https://kc.example/realms/kp');
    expect(cfg.oidc.clientId).toBe('spa-web');
  });

  it('falls back to env defaults when nothing is injected', () => {
    const cfg = loadAppConfig({} as unknown as Window);

    expect(cfg.bffBaseUrl).toBe('/bff');
    expect(cfg.oidc.clientId).toBe('spa-web');
  });

  it('fills missing injected fields from env (partial injection)', () => {
    const win = {
      __APP_CONFIG__: { bffBaseUrl: 'https://edge.example/bff' },
    } as unknown as Window;

    const cfg = loadAppConfig(win);

    expect(cfg.bffBaseUrl).toBe('https://edge.example/bff');
    // oidc は env 既定で補完される。
    expect(cfg.oidc.clientId).toBe('spa-web');
  });
});
