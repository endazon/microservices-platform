// FR-20, [[IADR-0270]] 決定 7 / ADR-0050: サーバの `ContentHash` は
// `DocumentBodyIntake.Fingerprint` ＝ **UTF-8 本文の SHA-256（小文字 hex）**である。
// プラグインも同じ計算をして、ローカルの内容がサーバと同じか（＝書かなくてよいか）を判定する。
//
// Web Crypto は Obsidian（Electron / モバイル）と Node ≥ 19 の両方に `globalThis.crypto.subtle` として
// 在るので、実装は 1 つで済む。テストは差し替え可能なように `Hasher` を関数型にしてある。

export type Hasher = (text: string) => Promise<string>;

/** 空文字列の SHA-256。サーバが `contentHash=null`（本文なし）を返したときの比較対象。 */
export const EMPTY_CONTENT_SHA256 =
  'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';

export const sha256Hex: Hasher = async (text) => {
  const bytes = new TextEncoder().encode(text);
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('');
};
