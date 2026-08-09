// Issue #126: BFF 境界のエラー種別。IADR-0009（存在秘匿）と整合し、画面は「拒否」と「不在」を
// 区別しない。404 は NotFound として扱い、存在の有無を推測させない。

export type ApiErrorKind =
  | 'unauthorized' // 401: 未認証/期限切れ → 再ログイン
  | 'validation' // 400: 入力/検証エラー（SC-09 ABAC ポリシー矛盾など。details にメッセージ）
  | 'conflict' // 409: 競合（楽観ロック・参照中削除など）
  | 'notFound' // 404: 不在または存在秘匿（IADR-0009）。画面は区別しない
  | 'forbidden' // 403: 権限不足（存在は秘匿しないケース。BFF 側の方針に従う）
  | 'server' // 5xx: サーバエラー
  | 'network' // 到達不能・タイムアウト
  | 'unknown';

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number | null;
  // FR-09, SC-09: 検証エラー（400）の詳細メッセージ群。矛盾・構文エラーを画面へ表示する用途。
  readonly details: string[];

  // FR-09, SC-09, #640: 解析済みの問題本文（400 / 409 のみ）。
  //
  // **`details` は人間可読な文字列だけを持つ**ので、**数値を画面が読めない**。
  // SC-09 は「削除前に**使用件数**を示す」と定めており、`usageCount` を
  // **翻訳済みの文へ差し込む**には数値そのものが要る（サーバの日本語をそのまま出すと
  // en ロケールで日本語が混ざる）。**本文を捨てずに載せる**のはそのためである。
  //
  // **`details` の意味は変えていない** —— 既存の呼び出し側（`toMessages`）は影響を受けない。
  readonly body: unknown;

  constructor(
    kind: ApiErrorKind,
    message: string,
    status: number | null = null,
    details: string[] = [],
    body: unknown = undefined,
  ) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = status;
    this.details = details;
    this.body = body;
  }

  /** HTTP ステータスから種別を導出する（IADR-0009: 404 は不在/秘匿を区別しない）。 */
  static fromStatus(status: number, details: string[] = [], body: unknown = undefined): ApiError {
    if (status === 401) return new ApiError('unauthorized', '認証が必要です。', status);
    if (status === 400)
      return new ApiError('validation', '入力内容に誤りがあります。', status, details, body);
    if (status === 403) return new ApiError('forbidden', '権限がありません。', status);
    if (status === 404) return new ApiError('notFound', '見つかりませんでした。', status);
    if (status === 409)
      return new ApiError('conflict', '競合が発生しました。', status, details, body);
    if (status >= 500) return new ApiError('server', 'サーバでエラーが発生しました。', status);
    return new ApiError('unknown', `要求が失敗しました（${status}）。`, status);
  }
}
