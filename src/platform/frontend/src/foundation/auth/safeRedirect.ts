// ADR-0031 / IADR-0124 決定 9: 外部由来の遷移先を「この SPA 内部のパス」へ限定する
// （オープンリダイレクト対策）。認証導線には SPA の外から戻ってくる遷移先が 2 経路ある。
//   1. `/login?from=...`（RequireAuth が付ける。URL に載るため利用者が書き換えられる）
//   2. OIDC の `state.returnTo`（認可サーバを往復して戻る）
// どちらも「攻撃者が任意の値を入れられる」前提で扱う。判定は本モジュール 1 箇所に集約する
// ——同じ条件を 2 か所へ書くと、片方だけ直したときに気付けない。

/**
 * 外部由来の値を、この SPA 内部の遷移先（パス ＋ クエリ ＋ フラグメント）として安全に解釈する。
 * 内部のパスとして解決できない場合は `null` を返す（呼び出し側が既定値へ倒す）。
 *
 * **文字列の前方一致では不足である**（実測）。`/\evil.com` は `'/'` で始まり `'//'` で始まらないため
 * 素朴な前方一致を通過するが、WHATWG URL 仕様（＝ブラウザの解釈）ではバックスラッシュが
 * スラッシュと同一視され `https://evil.com` へ解決される。したがって
 * **実際に URL として解決し、origin が一致することを確認する**。
 *
 * @param value 検査対象（型は不明。`validateSearch` には数値・配列・undefined も来うる）
 * @param origin 自分自身の origin。既定は実行中のページのもの
 */
export function toInternalPath(
  value: unknown,
  origin: string = window.location.origin,
): string | null {
  if (typeof value !== 'string' || value.length === 0) return null;
  // 先頭が '/' でないものは受け付けない（スキーム付き絶対 URL・スキーム相対・相対パスを一括で落とす）。
  if (!value.startsWith('/')) return null;

  let resolved: URL;
  try {
    resolved = new URL(value, origin);
  } catch {
    return null;
  }
  // 解決先が自分自身でなければ外部への遷移である。
  if (resolved.origin !== origin) return null;

  return `${resolved.pathname}${resolved.search}${resolved.hash}`;
}
