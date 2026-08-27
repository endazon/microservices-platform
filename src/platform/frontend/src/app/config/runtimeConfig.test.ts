import { describe, it, expect } from 'vitest';
import { assertSameOriginBffBaseUrl, loadAppConfig } from './runtimeConfig';

const PAGE_ORIGIN = 'https://app.example';

/**
 * 画面のオリジンを持つ偽 window。`loadAppConfig` は bffBaseUrl の同一オリジン性を
 * `win.location.origin` で判定するため、location を省略できない（省略できる形にすると
 * 検査が黙って無効化される）。
 */
function fakeWindow(config?: Record<string, unknown>): Window {
  return {
    __APP_CONFIG__: config,
    location: { origin: PAGE_ORIGIN },
  } as unknown as Window;
}

// Issue #126: 実行時 config は window.__APP_CONFIG__ を env より優先し、欠落項目は env で補う。
describe('loadAppConfig', () => {
  it('injected config takes precedence over env defaults', () => {
    const cfg = loadAppConfig(
      fakeWindow({
        bffBaseUrl: `${PAGE_ORIGIN}/bff`,
        oidc: { authority: 'https://kc.example/realms/kp', clientId: 'platform-spa' },
      }),
    );

    expect(cfg.bffBaseUrl).toBe(`${PAGE_ORIGIN}/bff`);
    expect(cfg.oidc.authority).toBe('https://kc.example/realms/kp');
    expect(cfg.oidc.clientId).toBe('platform-spa');
  });

  it('falls back to env defaults when nothing is injected', () => {
    const cfg = loadAppConfig(fakeWindow());

    expect(cfg.bffBaseUrl).toBe('/bff');
    expect(cfg.oidc.clientId).toBe('platform-spa');
  });

  it('fills missing injected fields from env (partial injection)', () => {
    const cfg = loadAppConfig(fakeWindow({ bffBaseUrl: `${PAGE_ORIGIN}/bff` }));

    expect(cfg.bffBaseUrl).toBe(`${PAGE_ORIGIN}/bff`);
    // oidc は env 既定で補完される。
    expect(cfg.oidc.clientId).toBe('platform-spa');
  });

  // Issue #136 / SC-10: 外部ツール導線 URL は実行時 config から注入する。
  it('reads injected opsLinks (SC-10 external tools)', () => {
    const cfg = loadAppConfig(
      fakeWindow({
        opsLinks: { grafanaUrl: 'https://grafana.example', jaegerUrl: 'https://jaeger.example' },
      }),
    );

    expect(cfg.opsLinks.grafanaUrl).toBe('https://grafana.example');
    expect(cfg.opsLinks.jaegerUrl).toBe('https://jaeger.example');
    // 未設定のツールは undefined（画面で非表示）。
    expect(cfg.opsLinks.kialiUrl).toBeUndefined();
  });

  it('treats empty-string opsLinks (unset envsubst vars) as undefined', () => {
    const cfg = loadAppConfig(fakeWindow({ opsLinks: { grafanaUrl: '', kialiUrl: '' } }));

    expect(cfg.opsLinks.grafanaUrl).toBeUndefined();
    expect(cfg.opsLinks.kialiUrl).toBeUndefined();
  });

  it('defaults opsLinks to empty when nothing is injected', () => {
    const cfg = loadAppConfig(fakeWindow());
    expect(cfg.opsLinks).toEqual({
      grafanaUrl: undefined,
      jaegerUrl: undefined,
      kialiUrl: undefined,
    });
  });

  // Issue #130 / SC-04: Wiki.js の基点 URL は実行時 config から注入する（空文字は未設定）。
  it('reads injected wikiBaseUrl and treats empty string as undefined', () => {
    const set = loadAppConfig(fakeWindow({ wikiBaseUrl: 'https://wiki.example' }));
    expect(set.wikiBaseUrl).toBe('https://wiki.example');

    const empty = loadAppConfig(fakeWindow({ wikiBaseUrl: '' }));
    expect(empty.wikiBaseUrl).toBeUndefined();

    expect(loadAppConfig(fakeWindow()).wikiBaseUrl).toBeUndefined();
  });

  // NFR, ADR-0032, IADR-0273, #439: 別オリジンの BFF は Cookie が送られず全要求が静かに未認証になる。
  // **起動時に落とす。**（wikiBaseUrl / opsLinks は別オリジンでよい —— Cookie を運ばないため。）
  it('rejects a cross-origin bffBaseUrl at load time', () => {
    expect(() => loadAppConfig(fakeWindow({ bffBaseUrl: 'https://edge.example/bff' }))).toThrow(
      /同一オリジン/,
    );
  });
});

describe('assertSameOriginBffBaseUrl', () => {
  // 陽性対照: 通ってよいものが通ること（否定形テストだけだと「常に throw」でも緑になる）。
  it.each(['/bff', '/', '/api/bff', `${PAGE_ORIGIN}/bff`, PAGE_ORIGIN])('accepts %s', (url) => {
    expect(() => assertSameOriginBffBaseUrl(url, PAGE_ORIGIN)).not.toThrow();
  });

  it.each([
    // 別ホスト。
    'https://edge.example/bff',
    // 同じホストで scheme が違う（オリジンは別物）。
    'http://app.example/bff',
    // 同じホストで port が違う。
    'https://app.example:8443/bff',
    // プロトコル相対（`//` 始まり）は相対パスに見えて別オリジンを指す。
    '//edge.example/bff',
  ])('rejects %s', (url) => {
    expect(() => assertSameOriginBffBaseUrl(url, PAGE_ORIGIN)).toThrow(/同一オリジン/);
  });

  it('rejects a value that is not a URL at all', () => {
    expect(() => assertSameOriginBffBaseUrl('http://', PAGE_ORIGIN)).toThrow(/解釈できない/);
  });
});
