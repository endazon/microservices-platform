import { ApiError } from '@foundation/api/ApiError';

// FR-09, SC-05/SC-06/SC-09（#183）: ApiError を画面表示用メッセージ群へ写像する共通ヘルパ。
// 各画面での重複を `@foundation/utils` へ単一情報源化する（IADR-0040: ApiError.details 統一）。
// ApiError の検証・競合詳細（Problem 本文由来）を優先し、詳細が無ければ ApiError の message、
// それも無ければ画面文脈に合わせた既定文言へフォールバックする。
export function toMessages(err: unknown, fallback: string): string[] {
  if (err instanceof ApiError && err.details.length > 0) return err.details;
  if (err instanceof ApiError && err.message) return [err.message];
  return [fallback];
}
