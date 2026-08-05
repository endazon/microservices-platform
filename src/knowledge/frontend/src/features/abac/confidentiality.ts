// FR-05/FR-09, SC-05/SC-06: 機密区分（ABAC 文書属性）の値集合。
//
// **画面ではなく語彙の単位で置く。** SC-05（文書の機密区分。必須）と SC-06（データソースの
// 既定の機密区分）が同じ値集合を使うため、どちらかの画面フォルダに置くともう一方が
// 「文書管理画面に依存するデータソース管理画面」になる。両者が別々に定数を持つと、
// 値集合が増えたときに片方だけ更新されて静かに割れる（旧実装は実際に 2 箇所へ複製していた）。
//
// 値は AuthorizationService の属性辞書（06_technical/07_abac-attribute-model の 4 値）に準拠する。
// **表示名は与えない**——計画（hi-fi / SC-05 / SC-09）に現れるのは `internal`＝「社内限」と
// `confidential`＝「秘」の 2 値だけで、残り 2 値の表示名はどこにも無い。実装が決めるとそれが
// 事実上の用語定義になるため生値を出す（SC-03 の attributes.ts と同じ判断。planning#197 で裁定待ち）。

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
