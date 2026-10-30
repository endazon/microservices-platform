import { ApiError } from '@foundation/api/ApiError';

// FR-17, SC-19, ADR-0034 決定 8, [[IADR-0655]] (#2343): リンク作成の失敗の分類（純関数）。
//
// 🔴 **404 は「見つかりません」の 1 種類だけ**にする。後段は権限外・不存在・起点への書き込み権限なしを
//   すべて 404 に倒している（存在秘匿）。画面が理由を推し量って言い分けると、その時点で存在が漏れる。
// 409 `edge_exists` と 400 `unknown_edge_type` は利用者が直せる理由なので、本文の `error` で言い分ける
// （状態コードだけで判定しない —— 400 は検証エラー一般の器である）。

export type LinkFailure = 'notFound' | 'exists' | 'unknownType' | 'other';

export function linkFailureOf(error: unknown): LinkFailure {
  if (!(error instanceof ApiError)) return 'other';
  if (error.status === 404) return 'notFound';
  const code = (error.body as { error?: unknown } | undefined)?.error;
  if (error.status === 409 && code === 'edge_exists') return 'exists';
  if (error.status === 400 && code === 'unknown_edge_type') return 'unknownType';
  return 'other';
}
