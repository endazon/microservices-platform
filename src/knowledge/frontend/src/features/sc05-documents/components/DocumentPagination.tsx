import { useId } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Button, Label, Select } from '@platform/ui';
import { DOCUMENT_PAGE_SIZES } from '../api/useDocumentAdmin';
import { pageNumbers } from './pageNumbers';

// SC-05, FR-06（#1923 段 1）: 文書一覧のページ送り（[[IADR-0537]]）。
//
// 送り方は**オフセット**である（BFF の `offset` / `limit`）。総件数（`total`）は BFF が ABAC で絞った後の件数であり、
// ここは受け取った数を描くだけで、権限の有無を推し量る表示を足さない。
// 画面の部品だけで組む（`Button` / `Select` / `Label`）。共有 UI へ新しいプリミティブは足さない
// （使うのは本画面だけで、段 2 の絞り込み・並べ替えと並べて形が決まるまで共有へ出さない）。

interface DocumentPaginationProps {
  total: number;
  offset: number;
  limit: number;
  /** 取得中は操作を止める（連打で要求が積み上がらないように）。 */
  busy?: boolean;
  onChange: (next: { offset: number; limit: number }) => void;
}

export function DocumentPagination({
  total,
  offset,
  limit,
  busy,
  onChange,
}: DocumentPaginationProps) {
  const { t } = useLingui();
  const sizeId = useId();
  const totalPages = Math.max(1, Math.ceil(total / limit));
  const current = Math.min(Math.floor(offset / limit) + 1, totalPages);
  const from = total === 0 ? 0 : Math.min(offset + 1, total);
  const to = Math.min(offset + limit, total);
  const goTo = (page: number) => onChange({ offset: (page - 1) * limit, limit });

  return (
    <div className="mt-n3 flex flex-wrap items-center justify-between gap-n2 text-sm">
      {/* 総件数と表示範囲。読み上げにも同じ文で伝える（`role="status"` で更新を知らせる）。 */}
      <p role="status" className="text-fg-muted">
        <Trans>
          全 {total} 件中 {from}–{to} 件
        </Trans>
      </p>

      <div className="flex flex-wrap items-center gap-n2">
        <span className="flex items-center gap-2">
          <Label htmlFor={sizeId}>
            <Trans>表示件数</Trans>
          </Label>
          {/* 件数を変えたら先頭へ戻す（同じ offset のまま件数だけ変えると、見ていた行の位置が読めなくなる）。 */}
          <Select
            id={sizeId}
            selectSize="sm"
            className="w-20"
            value={limit}
            disabled={busy}
            onChange={(e) => onChange({ offset: 0, limit: Number(e.target.value) })}
          >
            {DOCUMENT_PAGE_SIZES.map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
          </Select>
        </span>

        <nav aria-label={t`ページ送り`} className="flex flex-wrap items-center gap-1">
          <Button
            type="button"
            size="sm"
            disabled={busy || current <= 1}
            onClick={() => goTo(current - 1)}
          >
            <Trans>前へ</Trans>
          </Button>
          {pageNumbers(current, totalPages).map((p, i) =>
            p === 'gap' ? (
              <span key={`gap-${i}`} aria-hidden className="px-1 text-fg-muted">
                …
              </span>
            ) : (
              <Button
                key={p}
                type="button"
                size="sm"
                variant={p === current ? 'primary' : undefined}
                aria-current={p === current ? 'page' : undefined}
                aria-label={t`${p} ページ目`}
                disabled={busy}
                onClick={() => goTo(p)}
              >
                {p}
              </Button>
            ),
          )}
          <Button
            type="button"
            size="sm"
            disabled={busy || current >= totalPages}
            onClick={() => goTo(current + 1)}
          >
            <Trans>次へ</Trans>
          </Button>
        </nav>
      </div>
    </div>
  );
}
