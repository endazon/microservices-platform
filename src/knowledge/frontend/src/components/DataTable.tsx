import { useMemo } from 'react';
import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react';
import {
  createSortedRowModel,
  rowSortingFeature,
  tableFeatures,
  useTable,
} from '@tanstack/react-table';
import type { ColumnDef, RowData } from '@tanstack/react-table';
import {
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';

// ADR-0031 §採用技術一覧「テーブル = TanStack Table」/ IADR-0121 決定 1 の第 4 段（#788）。
//
// ■ 役割分担
//   TanStack Table は**ヘッドレス**である。行モデルと並べ替えの状態だけを持ち、
//   `<table>` の意味づけと見た目は `@platform/ui` の Table 一式が持つ（IADR-0125 決定 1 が
//   「意味づけ（ソート・ページング）は入れない——それは feature の関心であり第 4 段の範囲」と
//   明示している）。**本コンポーネントがその第 4 段側の受け皿である。**
//
// ■ v9 を採る理由
//   `@tanstack/react-table` の `latest` は 9.1.2 である（実測 2026-08-23）。v9 は v8 と API が違い
//   （`useReactTable` → `useTable`、行モデルは `tableFeatures` のスロット登録）、v8 を選ぶと
//   Renovate（採用済み）の更新と衝突し続ける。API はパッケージ同梱の
//   `skills/getting-started/SKILL.md`（v9.1.2）を出典とする。
//
// ■ 入れないもの
//   ページング・列の表示切替・行選択は**登録しない**。v9 は機能を登録しない限り状態も API も
//   生えない設計であり、使わない機能を登録すると「動くはずの操作が無い」表になる。
//   必要になった画面がその機能を足す。
//
// ■ アクセシビリティ（INDEX 決定 21「色だけで意味を持たせない」）
//   並び順は **`aria-sort` ＋ 方向アイコン（矢印の向き＝形）** で表す。色は使わない。
//   見出しはボタンにし、キーボードだけで並べ替えられるようにする。

/** 並べ替えだけを登録した機能集合。**モジュールスコープに置く**——毎描画で作ると行モデルが毎回無効になる。 */
const features = tableFeatures({
  rowSortingFeature,
  sortedRowModel: createSortedRowModel(),
});

export type DataTableColumns<TData extends RowData> = ColumnDef<typeof features, TData>[];

export interface DataTableProps<TData extends RowData> {
  /** 表の説明（読み上げ用）。**翻訳済みの文字列**を渡す（プリミティブは文言を持たない）。 */
  caption: string;
  columns: DataTableColumns<TData>;
  data: TData[];
  /** 見出しボタンの補助説明（読み上げ用）。「並べ替え」等の翻訳済み文字列を渡す。 */
  sortHint: string;
}

/** `aria-sort` の値。未ソートの列は `none`（属性ごと落とすと「並べ替えられる列」だと分からない）。 */
function ariaSort(direction: false | 'asc' | 'desc'): 'ascending' | 'descending' | 'none' {
  if (direction === 'asc') return 'ascending';
  if (direction === 'desc') return 'descending';
  return 'none';
}

export function DataTable<TData extends RowData>({
  caption,
  columns,
  data,
  sortHint,
}: DataTableProps<TData>) {
  // 参照の安定を保つ（v9 の指針。新しい参照を毎描画で渡すと行モデルが毎回組み直される）。
  const stableData = useMemo(() => data, [data]);
  const table = useTable({ features, columns, data: stableData });

  return (
    <Table>
      <TableCaption>{caption}</TableCaption>
      <TableHead>
        {table.getHeaderGroups().map((group) => (
          <TableRow key={group.id}>
            {group.headers.map((header) => {
              const direction = header.column.getIsSorted();
              const canSort = header.column.getCanSort();
              return (
                <TableHeaderCell
                  key={header.id}
                  aria-sort={canSort ? ariaSort(direction) : undefined}
                >
                  {header.isPlaceholder ? null : canSort ? (
                    <button
                      type="button"
                      className="flex items-center gap-1 hover:underline"
                      title={sortHint}
                      onClick={header.column.getToggleSortingHandler()}
                    >
                      <table.FlexRender header={header} />
                      <SortIcon direction={direction} />
                    </button>
                  ) : (
                    <table.FlexRender header={header} />
                  )}
                </TableHeaderCell>
              );
            })}
          </TableRow>
        ))}
      </TableHead>
      <TableBody>
        {table.getRowModel().rows.map((row) => (
          <TableRow key={row.id}>
            {row.getAllCells().map((cell) => (
              <TableCell key={cell.id}>
                <table.FlexRender cell={cell} />
              </TableCell>
            ))}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** 並び順の標識。**形（矢印の向き）で表す**——色だけで意味を持たせない（INDEX 決定 21）。 */
function SortIcon({ direction }: { direction: false | 'asc' | 'desc' }) {
  if (direction === 'asc') return <ArrowUp className="size-3.5" aria-hidden />;
  if (direction === 'desc') return <ArrowDown className="size-3.5" aria-hidden />;
  return <ArrowUpDown className="size-3.5 text-[--color-fg-muted]" aria-hidden />;
}
