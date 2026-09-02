import { EndpointError, normalizeEndpoint } from './endpoint.ts';

describe('normalizeEndpoint', () => {
  // FR-20, 08_data-egress-policy 許容条件 2: 同期トークンは https でしか送らない
  it('https の接続先は末尾のスラッシュを落として受け付ける', () => {
    expect(normalizeEndpoint('https://kb.example.co.jp/')).toBe('https://kb.example.co.jp');
    expect(normalizeEndpoint('  https://kb.example.co.jp/api// ')).toBe(
      'https://kb.example.co.jp/api',
    );
  });

  // FR-20: loopback（port-forward）だけは http を許す（陽性対照）
  it('localhost / 127.0.0.1 は http でも受け付ける', () => {
    expect(normalizeEndpoint('http://localhost:18093')).toBe('http://localhost:18093');
    expect(normalizeEndpoint('http://127.0.0.1:18093/')).toBe('http://127.0.0.1:18093');
  });

  // FR-20, 08_data-egress-policy 許容条件 2: loopback 以外の http は拒否（陰性）
  it('loopback 以外の http は EndpointError になる', () => {
    expect(() => normalizeEndpoint('http://kb.example.co.jp')).toThrow(EndpointError);
    expect(() => normalizeEndpoint('http://10.0.0.5:8080')).toThrow(/https/);
  });

  // FR-20: 未設定・不正な形・クエリ付きは設定不備として止める
  it('空・不正な形・クエリやフラグメント付きは EndpointError になる', () => {
    expect(() => normalizeEndpoint('')).toThrow(/未設定/);
    expect(() => normalizeEndpoint('kb.example.co.jp')).toThrow(EndpointError);
    expect(() => normalizeEndpoint('ftp://kb.example.co.jp')).toThrow(EndpointError);
    expect(() => normalizeEndpoint('https://kb.example.co.jp/?x=1')).toThrow(EndpointError);
    expect(() => normalizeEndpoint('https://kb.example.co.jp/#top')).toThrow(EndpointError);
  });
});
