// SC-01, UC-01 基本フロー 5 / 05_screens §共通シェル（右レール AI チャット）: 出典（Wiki／原本リンク）の型と種別判定。
//
// ［2026-09-12 / UI/UX 改善 A-8］`knowledge/.../sc01-search/types/citations.ts` から**移動**した。
// 右レールの AI チャット（platform の foundation）も `citations` イベントを購読して出典を描くため、
// 型と判定を foundation 側に置き、SC-01（knowledge）は `@foundation/ai-chat/citations` を import する
// （knowledge → foundation は許可された向き。逆向きは `import/no-restricted-paths` が止める）。
//
// `CitationDto` は出典の種別を持たない。**判定は権限内の Wiki 台帳（`GET /bff/wiki/pages`）に
// その文書 ID が載っているか**で行う（画面仕様書 SC-01 §出典の種別判定。knowledge の `lib/wiki-pages`）。
// 台帳は knowledge ユニットの口なので、**判定に使う集合は呼び出し側が渡す**（foundation は台帳を引かない）。
//
// ［2026-09-03 / #1200 / IADR-0365 決定 1］従前は `sourceUri` が実行時 config `wikiBaseUrl` で始まるかで
// 判定していた。ADR-0073 決定 1 が stg/prod で `WIKI_BASE_URL` を**設定しない**と定めたため、その判定は
// 本番で一度も真にならなかった。台帳は後段の ABAC を通った権限内のメタデータだけを返すので、
// 「載っている ＝ 利用者が SC-04 で開ける」が成り立つ。`sourceUri` はもう見ない。

/**
 * 出典の種別。`document` = 正規化文書（SC-03 へ）、`wiki` = Wiki ページ（SC-04 へ）、
 * `personal` = 個人資料（SC-19。05_screens §SC-01「組織文書と個人資料をアイコン＋ラベルの併用で区別」）。
 *
 * `personal` は**表示側の語彙**として持つ。`CitationDto` にはまだ個人資料の印が無く
 * （FR-19 / FR-21 は IADR-0119 決定 1 の着手保留）、`citationKind()` はこれを返さない。
 * 契約に印が入ったとき、判定だけを足せば描画は揃っている。
 */
export type CitationKind = 'document' | 'wiki' | 'personal';

/** 出典 1 件（BFF の `CitationDto` に対応）。 */
export interface AskCitation {
  /** 回答本文中の `[n]` と対応する番号（脚注方式の対応印）。 */
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

/**
 * 出典と本文の対応印（裁定 6・脚注方式）のアンカー ID。
 *
 * サーバは `[n]` の位置を返さないため、本文中の `[n]` を `<sup><a href="#…">` に、出典の各行を
 * 同じ ID の要素にして結ぶ。**接頭辞は回答ごとに変える**——1 画面に複数の回答（SC-01 の履歴・
 * 右レールの往復）が並ぶため、`cite-1` だけでは同じ ID が衝突する。
 * ID に使える文字へ丸めるのは、接頭辞にルートの pathname 等が混ざっても壊れないようにするため。
 */
export function citationAnchorId(prefix: string, number: number): string {
  return `${prefix.replace(/[^A-Za-z0-9_-]/g, '_')}-cite-${number}`;
}
