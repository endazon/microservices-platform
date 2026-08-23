import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DataTable } from './DataTable';
import type { DataTableColumns } from './DataTable';

// ADR-0031 §採用技術一覧（テーブル = TanStack Table）/ IADR-0121 決定 1 の第 4 段（#788）:
// ヘッドレスの表が **`@platform/ui` の表構造を保ったまま**並べ替えを足すことを固定する。

interface Row extends Record<string, unknown> {
  term: string;
  count: number;
}

const COLUMNS: DataTableColumns<Row> = [
  { id: 'term', accessorKey: 'term', header: '検索語' },
  { id: 'count', accessorKey: 'count', header: '件数' },
];

const DATA: Row[] = [
  { term: '経費', count: 4 },
  { term: '就業規則', count: 9 },
  { term: '育休', count: 1 },
];

function renderTable() {
  return render(<DataTable caption="一覧" sortHint="並べ替え" columns={COLUMNS} data={DATA} />);
}

const bodyTerms = () =>
  within(screen.getAllByRole('rowgroup')[1])
    .getAllByRole('row')
    .map((row) => within(row).getAllByRole('cell')[0].textContent);

describe('DataTable', () => {
  // IADR-0125 決定 1: 表構造の a11y は `@platform/ui` が持つ。載せ替えで壊していないこと。
  it('keeps the table structure and the accessible caption', () => {
    renderTable();
    expect(screen.getByRole('table', { name: '一覧' })).toBeInTheDocument();
    expect(screen.getAllByRole('columnheader')).toHaveLength(2);
    expect(bodyTerms()).toEqual(['経費', '就業規則', '育休']);
  });

  // INDEX 決定 21「色だけで意味を持たせない」: 並び順は `aria-sort` で読める。
  // 未ソートの列も `none` を持つ（属性ごと落とすと「並べ替えられる列」だと分からない）。
  //
  // **数値列の初手は降順である**（TanStack Table の `sortDescFirst` 既定。実測 v9.1.2）。
  // 既定を上書きしないのは、件数の列で「多い順」から始まるのが読み手の期待に合うためである。
  it('exposes the sort direction through aria-sort', async () => {
    const user = userEvent.setup();
    renderTable();
    const header = screen.getByRole('columnheader', { name: /件数/ });
    expect(header).toHaveAttribute('aria-sort', 'none');

    await user.click(within(header).getByRole('button'));
    expect(header).toHaveAttribute('aria-sort', 'descending');
    expect(bodyTerms()).toEqual(['就業規則', '経費', '育休']);

    await user.click(within(header).getByRole('button'));
    expect(header).toHaveAttribute('aria-sort', 'ascending');
    expect(bodyTerms()).toEqual(['育休', '経費', '就業規則']);
  });

  // #788: 見出しはボタンであり、キーボードだけで並べ替えられる。
  it('sorts from the keyboard', async () => {
    const user = userEvent.setup();
    renderTable();
    const header = screen.getByRole('columnheader', { name: /検索語/ });
    within(header).getByRole('button').focus();
    await user.keyboard('{Enter}');
    // 文字列列の初手は昇順（`sortDescFirst` は数値列にだけ効く）。
    expect(header).toHaveAttribute('aria-sort', 'ascending');
  });

  // #788: 入力（TanStack Query のキャッシュ配列）を並べ替えで破壊しない。
  it('does not mutate the input data', async () => {
    const user = userEvent.setup();
    renderTable();
    await user.click(
      within(screen.getByRole('columnheader', { name: /件数/ })).getByRole('button'),
    );
    expect(DATA.map((r) => r.term)).toEqual(['経費', '就業規則', '育休']);
  });
});
