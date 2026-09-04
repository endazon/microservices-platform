// SC-04, UC-07, FR-13 / IADR-0124 決定 3 / IADR-0367 決定 4 (#1200): `/wiki` の検索パラメータ。
//
// ルートは計画（05_screens §SC-04 §ルート）どおり `/wiki` の 1 本で、**どのページを開いているか・
// 何を検索しているかは URL が単一情報源**である（SC-02 の `?q=` / SC-19 の `?tab=` と同じ作法）。
// 共有・再読込・戻るで同じ画面になる性質を、クライアントストアで二重に持たない。
//
// - `page`: ページのスラッグ。ページツリー・検索結果から本文を開く（`/bff/wiki/pages/{slug}`）。
// - `doc`: 文書 ID。SC-01 の出典・SC-03 の「Wiki で閲覧」から開く（`/bff/wiki/pages/by-doc/{id}`）。
//   **文書別ディープリンク**であり、SC-03 画面仕様書 §未決事項 4「ページ単位では飛べない」を解く。
// - `q`: 検索語（`/bff/wiki/search?q=`）。
//
// `page` と `doc` が同時に来たら `page` を優先する（ツリーで選び直した直後の URL に `doc` が残っても
// 選んだページが勝つ）。**どれも無ければ本文は描かない**（ツリーから選ばせる）。

export interface WikiSearch {
  page?: string;
  doc?: string;
  q?: string;
}

/** 空文字・文字列以外は「無い」に倒す（URL は外部由来）。 */
function optionalString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

export function validateWikiSearch(raw: Record<string, unknown>): WikiSearch {
  return {
    page: optionalString(raw.page),
    doc: optionalString(raw.doc),
    q: optionalString(raw.q),
  };
}
