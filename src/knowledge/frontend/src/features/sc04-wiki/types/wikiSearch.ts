// SC-04, UC-07, FR-13 / IADR-0124 決定 3 / IADR-0365 決定 4 (#1200): `/wiki` の検索パラメータ。
//
// ルートは計画（05_screens §SC-04 §ルート）どおり `/wiki` の 1 本で、**どのページを開いているか・
// 何を検索しているかは URL が単一情報源**である（SC-02 の `?q=` / SC-19 の `?tab=` と同じ作法）。
// 共有・再読込・戻るで同じ画面になる性質を、クライアントストアで二重に持たない。
//
// - `doc`: 文書 ID。**ページを指す唯一の鍵**（`/bff/wiki/pages/by-doc/{id}`）。ページツリー・検索結果・SC-01 の出典・
//   SC-03 の「Wiki で閲覧」・本文の中のページ間リンクのすべてがこれで開く。題名を変えても切れない。
// - `page`: ページのスラッグ。**旧形式の共有 URL の互換のためだけに受ける**（`/bff/wiki/pages/{slug}`）。
//   画面は `page` を作らない。`page` だけで来たら slug で引き、1 件に決まって開けたら URL を `doc` へ書き換える
//   （`replace`。後段は同じ slug が複数の文書に一致すれば 404 を返す）。
// - `q`: 検索語（`/bff/wiki/search?q=`）。
//
// 🔴 FR-13, ADR-0091 決定 1, IADR-0644 (#2108): `page` と `doc` が同時に来たら **`doc` が勝つ**（URL から `page` を外す）。
// 従前（IADR-0365 決定 4）は `page` が勝ち、改題で古くなった `page=` が正しい `doc=` に勝っていた。
// **どれも無ければ本文は描かない**（ツリーから選ばせる）。

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
