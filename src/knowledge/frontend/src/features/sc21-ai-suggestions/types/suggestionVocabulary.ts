import type { EdgeTypeCatalogItem } from '@foundation/api/generated/bff.schemas';

// SC-21, UC-10, FR-18: AI 提案一覧の語彙と写像（計画 13_frontend-stack §ディレクトリ構成 の `types/`）。
//
// **React にも router にも依存しない純粋な定義だけを置く。** 表示文言（Lingui のマクロ）は
// 画面側が持つ —— ここに置くとカタログの抽出単位が語彙ファイルへ散る。
//
// **BFF の DTO をここへ手書きしない。** BFF 由来の型は orval の生成物を import する
// （ADR-0031 §基本方針「手書きクライアント禁止」）。ここに在るのは、URL の検索パラメータと
// 画面の都合で組み立てる型だけである。

/**
 * 状態フィルタの選択肢（05_screens §SC-21 入力/バリデーション）。
 *
 * 🔴 `all` は**状態の値ではなくフィルタの解除**である（後段の `AiSuggestionEndpoints.AnyState`）。
 */
export const STATE_OPTIONS = ['pending', 'approved', 'rejected', 'all'] as const;
export type StateOption = (typeof STATE_OPTIONS)[number];

/**
 * 種類フィルタの選択肢（すべて／リンク／タグ）。
 *
 * 🔴 **リンク提案とタグ提案で画面を分けない**（05_screens §SC-21「描いてはいけないもの」）。
 * 分けると片方が忘れられるためである。`all` は「絞らない」を意味し、後段へは送らない。
 */
export const KIND_OPTIONS = ['all', 'link', 'tag'] as const;
export type KindOption = (typeof KIND_OPTIONS)[number];

export interface AiSuggestionSearch {
  state: StateOption;
  kind: KindOption;
}

/**
 * URL の検索パラメータを正規化する（ルートの `validateSearch` の実体）。
 *
 * URL は外部由来なので、**未知の値は既定へ倒す** —— 選択肢しか無い UI に「エラー状態」を
 * 持ち込まない（手打ちの `?state=maybe` で画面を壊さない）。値域の防壁はサーバ（400）に在り、
 * ここは丸めるだけである。**既定は `pending`**（URL に無くても `pending` である）。
 */
export function normalizeAiSuggestionSearch(raw: Record<string, unknown>): AiSuggestionSearch {
  return {
    state: STATE_OPTIONS.find((s) => s === raw.state) ?? 'pending',
    kind: KIND_OPTIONS.find((k) => k === raw.kind) ?? 'all',
  };
}

/**
 * 状態バッジの色。**色だけで意味を持たせない**（`StatusBadge` が色 ＋ アイコン ＋ テキストを
 * 強制する）ため、ここが決めるのは色だけであり、意味はラベルが担う。
 */
export function suggestionTone(state: string): 'success' | 'danger' | 'neutral' {
  if (state === 'approved') return 'success';
  if (state === 'rejected') return 'danger';
  return 'neutral';
}

/**
 * 辺の型 ID → 表示名の辞書。
 *
 * 🔴 **表示名は辞書側で解決する**（ADR-0033 決定 9）—— 型を改名しても一覧が追随するためである。
 * 提案の DTO は `edgeTypeId` しか持たない。**引けないときに ID を出さない**判断は画面側が持つ
 * （GUID を利用者へ見せても判断の役に立たない）。
 */
export function edgeTypeNameMap(catalog: EdgeTypeCatalogItem[]): Map<string, string> {
  return new Map(catalog.map((type) => [type.id, type.name]));
}
