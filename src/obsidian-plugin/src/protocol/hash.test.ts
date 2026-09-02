// @vitest-environment node
// Web Crypto（globalThis.crypto.subtle）は Node の実体で確かめる。jsdom は subtle を持たないことがある。
import { EMPTY_CONTENT_SHA256, sha256Hex } from './hash.ts';

describe('sha256Hex', () => {
  // FR-20, [[IADR-0270]] 決定 7: サーバの ContentHash（UTF-8 本文の SHA-256 小文字 hex）と同じ計算
  it('既知のベクタと一致する（空文字・abc）', async () => {
    await expect(sha256Hex('')).resolves.toBe(EMPTY_CONTENT_SHA256);
    await expect(sha256Hex('abc')).resolves.toBe(
      'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
    );
  });

  // FR-20: UTF-8 のバイト列で数える（'あ' は 3 バイト。ASCII の 'a' と別のハッシュになる）
  it('非 ASCII は UTF-8 のバイト列としてハッシュされる', async () => {
    const hiragana = await sha256Hex('あ');
    expect(hiragana).toMatch(/^[0-9a-f]{64}$/);
    expect(hiragana).not.toBe(await sha256Hex('a'));
  });
});
