// Issue #126: BFF 境界のエラー種別。IADR-0009（存在秘匿）と整合し、画面は「拒否」と「不在」を
// 区別しない。404 は NotFound として扱い、存在の有無を推測させない。

export type ApiErrorKind =
  | 'unauthorized' // 401: 未認証/期限切れ → 再ログイン
  | 'notFound' // 404: 不在または存在秘匿（IADR-0009）。画面は区別しない
  | 'forbidden' // 403: 権限不足（存在は秘匿しないケース。BFF 側の方針に従う）
  | 'server' // 5xx: サーバエラー
  | 'network' // 到達不能・タイムアウト
  | 'unknown';

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number | null;

  constructor(kind: ApiErrorKind, message: string, status: number | null = null) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = status;
  }

  /** HTTP ステータスから種別を導出する（IADR-0009: 404 は不在/秘匿を区別しない）。 */
  static fromStatus(status: number): ApiError {
    if (status === 401) return new ApiError('unauthorized', '認証が必要です。', status);
    if (status === 403) return new ApiError('forbidden', '権限がありません。', status);
    if (status === 404) return new ApiError('notFound', '見つかりませんでした。', status);
    if (status >= 500) return new ApiError('server', 'サーバでエラーが発生しました。', status);
    return new ApiError('unknown', `要求が失敗しました（${status}）。`, status);
  }
}
