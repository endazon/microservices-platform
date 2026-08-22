// FR-05, UC-04, SC-06: 所管部門（ABAC 文書属性 `department`）の語彙。
//
// **画面ではなく語彙の単位で置く**（`confidentiality.ts` と同じ理由）。`department` は SC-06 の
// 「既定の部門」だけでなく、SC-01 / SC-08 の対象範囲軸（`scope-filter/scopeSelection.ts` の `SCOPE_AXES`）
// でも同じキーを指す。どちらかの画面フォルダに置くと、もう一方が「その画面に依存する画面」になる。
//
// 計画の正は `06_technical/09_datasource-connectors.md` §システム投入経路での `owner` / `department` /
// `lifecycle`（確定・2026-08-15）と `06_technical/07_abac-attribute-model.md` §文書の基本属性である。
//
// **値集合は持たない。** `confidentiality`（4 値）と違い、部門コードの値域は計画に列挙が無く
// （07_abac-attribute-model は「部門コード（人事/経理/開発 等）」と例示するのみ）、SC-09 の属性辞書が
// 管理する。**実装が値集合を決めると事実上の用語定義になる**ため、SC-06 では自由入力とする。
//
// **フォルダ名から部門コードを推定しない。** 計画の 3 段（① ソースから解決 → ② データソースの既定属性 →
// ③ 予約値）のうち ① の写像規則は未裁定である（planning#372）。ここが埋めるのは ② だけである。

/** ABAC 属性辞書における所管部門のキー。 */
export const DEPARTMENT_KEY = 'department';

/**
 * 解決できなかったときに後段が入れる**予約値**（既定値ではない）。
 *
 * 計画は「`system` / `unassigned` は解決できなかったことの記録であり、恒久的に積み上がるなら
 * コネクタが部門を運んでいないという報告である」と定める。件数は環流債務の測定値として読む
 * （バックエンド `DataSource.UnresolvedDepartment` と同じ値。IADR-0199）。
 *
 * 画面では「未入力ならこの値になる」ことを利用者へ伝えるためだけに使う。**この値を送らない**
 * —— 送ると「解決できなかった」と「管理者が明示的にそう指定した」の区別が消える。
 */
export const UNRESOLVED_DEPARTMENT = 'unassigned';
