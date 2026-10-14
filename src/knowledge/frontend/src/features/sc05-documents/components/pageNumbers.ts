// SC-05, FR-06, IADR-0537（#1923 段 1）: 文書一覧のページ送りに出すページ番号。

/** 表示するページ番号の並び。先頭・末尾・現在の前後 1 つを出し、間は省略する（`'gap'`）。1 始まり。 */
export function pageNumbers(current: number, totalPages: number): (number | 'gap')[] {
  const wanted = new Set([1, totalPages, current - 1, current, current + 1]);
  const pages = [...wanted].filter((p) => p >= 1 && p <= totalPages).sort((a, b) => a - b);
  const out: (number | 'gap')[] = [];
  for (const p of pages) {
    const prev = out[out.length - 1];
    if (typeof prev === 'number' && p - prev > 1) out.push('gap');
    out.push(p);
  }
  return out;
}
