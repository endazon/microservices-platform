// FR-05/FR-09, SC-05/SC-06: 機密区分（ABAC 文書属性）の値集合。
//
// **画面ではなく語彙の単位で置く。** SC-05（文書の機密区分。必須）と SC-06（データソースの
// 既定の機密区分）が同じ値集合を使うため、どちらかの画面フォルダに置くともう一方が
// 「文書管理画面に依存するデータソース管理画面」になる。両者が別々に定数を持つと、
// 値集合が増えたときに片方だけ更新されて静かに割れる（旧実装は実際に 2 箇所へ複製していた）。
//
// 値は AuthorizationService の属性辞書（06_technical/07_abac-attribute-model の 4 値）に準拠する。
// **表示名はここでは与えない**（生値を出す）。
//
// **［2026-08-10 追記 / #553］保留の理由は解消した。** 従前は「計画に現れる表示名が 4 値中 2 値だけで、
// 実装が決めると事実上の用語定義になる」ため生値を出していた（planning#197 で裁定待ち）。
// **利用者裁定 2026-08-05（質問票 第12回 Q7/Q8/Q30）で 4 値すべての表示名が確定した** ——
// 正は計画リポジトリ `project-planning` の用語集 `docs/glossary.md` である:
//   public＝公開 / internal＝社内限 / confidential＝秘 / restricted＝**取扱制限**
// **`restricted` は「極秘」ではない**（個人資料〔private-note〕が既定でこの区分を持つため、
// 「極秘」にすると本当に極秘の組織文書を見分けられなくなる）。
//
// **写像は本ファイルでは入れない。** 表示名を必要とするのは引用（`CitationDto`）側であり、
// **実装先は #541** である（#553 がそう指示している）。ここは値集合の単一情報源に留める。

/** 機密区分の許可値（05_screens「定義済み区分のみ」）。 */
export const CONFIDENTIALITY_VALUES = ['public', 'internal', 'confidential', 'restricted'] as const;

export type Confidentiality = (typeof CONFIDENTIALITY_VALUES)[number];

/** ABAC 属性辞書における機密区分のキー。 */
export const CONFIDENTIALITY_KEY = 'confidentiality';

/**
 * フェイルセーフ既定値。
 *
 * `public`（過剰公開）でも `restricted`（過剰制限）でもなく、社内文書の基準となる `internal` を採る
 * （バックエンド `DataSource.DefaultConfidentiality` と同じ値。IADR-0019）。
 */
export const DEFAULT_CONFIDENTIALITY: Confidentiality = 'internal';
