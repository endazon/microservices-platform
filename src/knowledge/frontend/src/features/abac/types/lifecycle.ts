// FR-05, UC-04, SC-06: ライフサイクル状態（ABAC 文書属性 `lifecycle`）の語彙。
//
// **画面ではなく語彙の単位で置く**（`confidentiality.ts` / `department.ts` と同じ理由）。`lifecycle` は
// SC-06 の「既定のライフサイクル状態」だけでなく、SC-05 の公開（`draft` → `active`）・アーカイブ
// （`archived`）でも同じキーを指す。どちらかの画面フォルダに置くと、もう一方が「その画面に依存する画面」になる。
//
// 計画の正は `06_technical/07_abac-attribute-model.md` §文書の基本属性（`lifecycle`＝状態・**必須**・
// 値域 `draft` / `active` / `archived`）と `06_technical/09_datasource-connectors.md` §システム投入経路での
// `owner` / `department` / `lifecycle`（確定・2026-08-15。`lifecycle` は同日追補）である。
//
// **`normalized` / `published` は語彙ではない。** 計画（05_screens §SC-05）が「状態の語彙は
// 07_abac-attribute-model の `lifecycle` 属性〔`draft` / `active` / `archived`〕を正とする。実装の端点名は
// `publish` / `archive` であり、環流記録が用いた `normalized` / `published` は**計画側の語彙ではない**」と
// 明記している。**端点名（動詞）と属性値（状態）を混ぜない。**
//
// **ソース側から解決する対応物は無い。** 計画の 3 段（① ソースから解決 → ② データソースの既定属性 →
// ③ 終端値）のうち、`lifecycle` は **① が構造的に存在しない**（ファイルの状態を 3 値へ写像できない）。
// ここが埋めるのは ② だけである。

/**
 * ライフサイクル状態の許可値（07_abac-attribute-model §文書の基本属性）。
 *
 * `confidentiality` と同じく**値集合を持つ**。`department`（部門コード）は計画に列挙が無いため
 * 自由入力にしたが、こちらは 3 値が明記されている。
 */
export const LIFECYCLE_VALUES = ['draft', 'active', 'archived'] as const;

export type Lifecycle = (typeof LIFECYCLE_VALUES)[number];

/** ABAC 属性辞書におけるライフサイクル状態のキー（バックエンド `DataSource.LifecycleKey` と同値）。 */
export const LIFECYCLE_KEY = 'lifecycle';

/**
 * 終端の**既定値**（バックエンド `DataSource.DefaultLifecycle` と同値。裁定 planning#361 / IADR-0199 決定 4）。
 *
 * **予約値ではない。** `system` / `unassigned` は「解決できなかったことの記録」だが、`active` は
 * **そう決めた値**であり、件数を環流債務として数えない。画面の文言でも「予約値」と書かない。
 *
 * **指定が無いときだけ効く**（計画 09_datasource-connectors）。したがって画面は「未指定」を
 * 表現できなければならず、**この値を既定の選択にしてはならない** —— 既定にすると
 * 「管理者が明示的に `active` を指定した」と「指定しなかった」の区別が消える。
 */
export const DEFAULT_LIFECYCLE: Lifecycle = 'active';
