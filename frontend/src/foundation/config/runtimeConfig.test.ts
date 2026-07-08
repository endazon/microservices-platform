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

  // Issue #136 / SC-10: 外部ツール導線 URL は実行時 config から注入する。
  it('reads injected opsLinks (SC-10 external tools)', () => {
    const win = {
      __APP_CONFIG__: {
        opsLinks: { grafanaUrl: 'https://grafana.example', jaegerUrl: 'https://jaeger.example' },
      },
    } as unknown as Window;

    const cfg = loadAppConfig(win);

    expect(cfg.opsLinks.grafanaUrl).toBe('https://grafana.example');
    expect(cfg.opsLinks.jaegerUrl).toBe('https://jaeger.example');
    // 未設定のツールは undefined（画面で非表示）。
    expect(cfg.opsLinks.kialiUrl).toBeUndefined();
  });

  it('treats empty-string opsLinks (unset envsubst vars) as undefined', () => {
    const win = {
      __APP_CONFIG__: { opsLinks: { grafanaUrl: '', kialiUrl: '' } },
    } as unknown as Window;

    const cfg = loadAppConfig(win);

    expect(cfg.opsLinks.grafanaUrl).toBeUndefined();
    expect(cfg.opsLinks.kialiUrl).toBeUndefined();
  });

  it('defaults opsLinks to empty when nothing is injected', () => {
    const cfg = loadAppConfig({} as unknown as Window);
    expect(cfg.opsLinks).toEqual({
      grafanaUrl: undefined,
      jaegerUrl: undefined,
      kialiUrl: undefined,
    });
  });
});
