/**
 * 日時の表示整形（画面共通）。
 *
 * SC-06 が最初に持ち、SC-02 の「更新日時」列（#536）が 2 つ目の利用者になったので foundation へ移した。
 * **同じ整形規則を 2 か所に置かない** —— 置くと `—`（値なし）の書き方が画面ごとに割れる。
 *
 * - 値が無い（`null` / `undefined` / 空文字）ときは **`—`** を返す。
 *   SC-02 では「索引がまだ日時を持たない」（[[IADR-0149]] 決定 3）がここに来る。**利用者へ索引の
 *   内部事情を見せない**ので、「日時が無い」と「まだ再索引していない」を画面で区別しない。
 * - 解釈できない文字列は**そのまま返す**（勝手に `—` へ潰すと、壊れた値が届いていることが見えなくなる）。
 * - ロケールは**利用者のブラウザ設定**に従う（`toLocaleString()`）。表示言語の切替 UI は持たない。
 */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? value : new Date(parsed).toLocaleString();
}
