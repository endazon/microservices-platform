import dayjs from 'dayjs';

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
 *
 * ADR-0031 §採用技術一覧「日付 = dayjs」/ IADR-0121 決定 1 の第 4 段（#788）:
 * 整形の実体を `Date.parse` ＋ `toLocaleString()` から **dayjs** へ移した。
 *
 * - **表記は固定書式（`YYYY/MM/DD HH:mm`）にする。** 旧実装はブラウザのロケールに従っており、
 *   同じ値が実行環境ごとに違う文字列になった。**固定できない表示は退行を検出できない**
 *   （日時列が壊れてもテストは書けない）。表示言語の切替 UI は持たない（IADR-0125 決定 7）ため、
 *   ロケール追従を捨てても利用者の選択肢は減らない。
 * - 妥当性の判定は `dayjs().isValid()` に一本化する（`Date.parse` の実装差を持ち込まない）。
 * - **秒は出さない。** 出しているのは一覧の「最終同期」「更新日時」であり、秒の桁は読み手の
 *   判断を何も変えないうえ、列幅だけを食う。
 */
/* eslint-disable-next-line lingui/no-unlocalized-strings --
   dayjs の**書式パターン**であって表示文言ではない（翻訳すると日時が壊れる）。
   空白を含む ASCII 文字列は規則の ignore に当たらないため、ここだけ明示的に外す。 */
const DATE_TIME_FORMAT = 'YYYY/MM/DD HH:mm';

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format(DATE_TIME_FORMAT) : value;
}
