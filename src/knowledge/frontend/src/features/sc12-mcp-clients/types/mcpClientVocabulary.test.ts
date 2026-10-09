import { describe, expect, it } from 'vitest';
import {
  isAllowedRedirectUri,
  parseRedirectUris,
  requiresRedirectUris,
  validateRegistration,
} from './mcpClientVocabulary';

// SC-12, UC-09, FR-16, ADR-0134 決定 1: 有人のリダイレクト URI の規則（純関数）。
//
// 🔴 最終の判定は後段（`RedirectUriRules`）が持つ。ここは送る前の写しであり、後段の試験
// （`RegisterMcpClientValidatorTests`）と同じ事例を並べて、写しが緩む・きつくなる退行を止める。
describe('redirect URI rules (SC-12)', () => {
  it.each([
    'https://agent.example.test/callback',
    'https://agent.example.test:8443/cb?x=1',
    'https://agent.example.test/cb',
    'http://127.0.0.1:53123/callback',
    'http://127.0.0.1:53123',
    'http://127.0.0.1:53123?x=1',
    'http://127.0.0.1:80/cb',
    'http://127.0.0.1:65535/cb',
    'http://[::1]:53123/callback',
    'http://[::1]:53123',
    // ［2026-10-09］port なしのループバックも通る（実行時の任意の port で戻す。後段と同じ並び）。
    'http://127.0.0.1/callback',
    'http://127.0.0.1/',
    'http://127.0.0.1',
    'http://127.0.0.1?x=1',
    'http://[::1]/callback',
    'http://[::1]/',
    'http://[::1]',
    'http://[::1]?x=1',
  ])('allows %s', (uri) => {
    expect(isAllowedRedirectUri(uri)).toBe(true);
  });

  it.each([
    'https://agent.example.test/*',
    'https://*.example.test/cb',
    '*',
    'http://agent.example.test/callback',
    'http://localhost:8080/callback',
    'http://127.0.0.2/callback',
    'http://127.0.0.1.evil.example/callback',
    'http://127.1/callback',
    'https://agent.example.test/cb#frag',
    'https://user@agent.example.test/cb',
    '/callback',
    'myapp://callback',
    'http://127.0.0.1:49152@evil.example/cb',
    'http://[::1]:49152@evil.example/cb',
    // 横取りの形（CVE-2024-8883）。port なしの登録に当てる 4 形 × 2 ホストも、利用者情報の拒否で写しの段から止める。
    'http://127.0.0.1:1@evil.example/cb',
    'http://127.0.0.1:@evil.example/cb',
    'http://127.0.0.1:49152:1@evil.example/cb',
    'http://[::1]:1@evil.example/cb',
    'http://[::1]:@evil.example/cb',
    'http://[::1]:49152:1@evil.example/cb',
    // port を書いたなら 1〜65535 の数字だけ（`:` だけ・`:0` は不可）。
    'http://127.0.0.1:/callback',
    'http://127.0.0.1:0/callback',
    'http://[::1]:/callback',
    'http://[::1]:0/callback',
    '',
  ])('rejects %s', (uri) => {
    expect(isAllowedRedirectUri(uri)).toBe(false);
  });

  // ［2026-10-09］port の有無は理由を分けない。port なしは通り、`:` だけ・`:0` は「不正」として名指しする。
  it.each([
    'http://127.0.0.1:/callback',
    'http://[::1]:0/callback',
    'http://127.0.0.1:@evil.example/cb',
  ])('reports %s as an invalid redirect URI', (uri) => {
    expect(
      validateRegistration({
        clientId: 'a',
        displayName: 'A',
        attributes: [],
        kind: 'interactive',
        redirectUris: [uri],
      }),
    ).toEqual(['redirect-uri-invalid']);
  });

  it('accepts a port-less loopback registration', () => {
    expect(
      validateRegistration({
        clientId: 'a',
        displayName: 'A',
        attributes: [],
        kind: 'interactive',
        redirectUris: ['http://127.0.0.1/callback', 'http://[::1]/callback'],
      }),
    ).toEqual([]);
  });

  it('parses one URI per line and drops blank lines and surrounding spaces', () => {
    expect(
      parseRedirectUris(' https://a.example.test/cb \r\n\n http://127.0.0.1:53123/cb'),
    ).toEqual(['https://a.example.test/cb', 'http://127.0.0.1:53123/cb']);
  });

  it('requires redirect URIs only for the attended kind', () => {
    expect(requiresRedirectUris('interactive')).toBe(true);
    expect(requiresRedirectUris('service-account')).toBe(false);
    const base = { clientId: 'a', displayName: 'A', attributes: [] };
    expect(validateRegistration({ ...base, kind: 'interactive' })).toEqual([
      'redirect-uris-required',
    ]);
    expect(
      validateRegistration({
        ...base,
        kind: 'service-account',
        attributes: [{ key: 'k', value: 'v' }],
      }),
    ).toEqual([]);
  });

  it('reports invalid, duplicated and too many redirect URIs', () => {
    const base = { clientId: 'a', displayName: 'A', attributes: [], kind: 'interactive' };
    expect(validateRegistration({ ...base, redirectUris: ['https://a.example.test/*'] })).toEqual([
      'redirect-uri-invalid',
    ]);
    expect(
      validateRegistration({
        ...base,
        redirectUris: ['https://a.example.test/cb', 'https://a.example.test/cb'],
      }),
    ).toEqual(['redirect-uri-duplicate']);
    const eleven = Array.from({ length: 11 }, (_, i) => `https://a.example.test/cb${i}`);
    expect(validateRegistration({ ...base, redirectUris: eleven })).toEqual([
      'redirect-uris-too-many',
    ]);
    expect(validateRegistration({ ...base, redirectUris: eleven.slice(0, 10) })).toEqual([]);
  });
});
