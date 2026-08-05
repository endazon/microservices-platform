// SC-01, UC-01 基本フロー 5: 出典（Wiki／原本リンク）付きで結果を返す。
//
// `CitationDto` は出典の種別を持たないため、`sourceUri` の形で判定する（画面仕様書 SC-01 §出典の種別判定）。
// 判定に使う wiki のベース URL は**実行時 config**（platform/frontend/public/config.js）であり、
// ビルドへ焼き込まない。未設定の環境では Wiki 由来を**推測しない**（すべて文書として扱う）。

/** 出典の種別。`document` = 正規化文書（SC-03 へ）、`wiki` = Wiki ページ（SC-04 へ）。 */
export type CitationKind = 'document' | 'wiki';

/** 出典 1 件（BFF の `CitationDto` に対応）。 */
export interface AskCitation {
  number: number;
  documentId: string;
  documentTitle: string;
  chunkId: string;
  sourceUri?: string | null;
  score: number;
  snippet: string;
}

/**
 * 出典の種別を判定する。
 *
 * `wikiBaseUrl` が空（未設定）のときは常に `document` を返す——「Wiki かもしれない」を
 * 推測で表示すると、利用者が到達できない導線（SC-04）へ送ることになる。
 */
export function citationKind(
  sourceUri: string | null | undefined,
  wikiBaseUrl: string | null | undefined,
): CitationKind {
  if (!sourceUri || !wikiBaseUrl) return 'document';
  return sourceUri.startsWith(wikiBaseUrl) ? 'wiki' : 'document';
}
