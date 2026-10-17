// SC-05, FR-06（#1923・[[IADR-0560]]）: 一覧の横のペインに出す本文の先頭の抜き書き（純関数）。

/** プレビューに出す本文の先頭の文字数（コードポイントで数える。サロゲートペアを割らない）。 */
export const PREVIEW_CHARS = 1000;

export function previewText(markdown: string): { text: string; truncated: boolean } {
  const chars = Array.from(markdown.trim());
  return chars.length > PREVIEW_CHARS
    ? { text: chars.slice(0, PREVIEW_CHARS).join(''), truncated: true }
    : { text: chars.join(''), truncated: false };
}
