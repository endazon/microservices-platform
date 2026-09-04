// SC-01, UC-01 基本フロー 5: 出典（Wiki／原本リンク）付きで結果を返す。
//
// `CitationDto` は出典の種別を持たない。**判定は権限内の Wiki 台帳（`GET /bff/wiki/pages`）に
// その文書 ID が載っているか**で行う（画面仕様書 SC-01 §出典の種別判定。`lib/wiki-pages`）。
//
// ［2026-09-03 / #1200 / IADR-0365 決定 1］従前は `sourceUri` が実行時 config `wikiBaseUrl` で始まるかで
// 判定していた。ADR-0073 決定 1 が stg/prod で `WIKI_BASE_URL` を**設定しない**と定めたため、その判定は
// 本番で一度も真にならなかった。台帳は後段の ABAC を通った権限内のメタデータだけを返すので、
// 「載っている ＝ 利用者が SC-04 で開ける」が成り立つ。`sourceUri` はもう見ない。

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
 * 台帳が未取得・取得失敗（`undefined`）のときは常に `document` を返す——「Wiki かもしれない」を
 * 推測で表示すると、利用者が到達できない導線（SC-04）へ送ることになる。`documentId` は常にあるので
 * SC-03 へは必ず辿れる。
 */
export function citationKind(
  documentId: string,
  wikiDocumentIds: ReadonlySet<string> | undefined,
): CitationKind {
  if (!wikiDocumentIds) return 'document';
  return wikiDocumentIds.has(documentId) ? 'wiki' : 'document';
}
