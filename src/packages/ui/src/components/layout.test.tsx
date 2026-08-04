import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Card, CardContent, CardHeader, CardTitle } from './Card';
import {
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from './Table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from './Tabs';

// ADR-0031 / IADR-0125 決定 1: 区画（SC-03 属性パネル・SC-08 結果パネル）・
// 表（SC-02 結果テーブル・SC-06 ソース一覧・SC-07 ジョブ一覧）・
// タブ（SC-09 属性体系 / タグ辞書 / 辺の型 / ポリシー定義）の回帰。

describe('Card', () => {
  it('見出しと中身を持つ区画として描画される', () => {
    render(
      <Card>
        <CardHeader>
          <CardTitle>属性・タグ</CardTitle>
        </CardHeader>
        <CardContent>機密区分: 社内</CardContent>
      </Card>,
    );
    expect(screen.getByRole('heading', { level: 2, name: '属性・タグ' })).toBeInTheDocument();
    expect(screen.getByText('機密区分: 社内')).toBeInTheDocument();
  });

  it('見出しレベルは as で変えられる（入れ子の画面で階層が壊れないようにする）', () => {
    render(<CardTitle as="h3">バージョン履歴</CardTitle>);
    expect(screen.getByRole('heading', { level: 3, name: 'バージョン履歴' })).toBeInTheDocument();
  });
});

describe('Table', () => {
  function Fixture() {
    return (
      <Table>
        <TableCaption>データソース一覧</TableCaption>
        <TableHead>
          <TableRow>
            <TableHeaderCell>ソース</TableHeaderCell>
            <TableHeaderCell>同期状態</TableHeaderCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell>共有フォルダ</TableCell>
            <TableCell>同期済み</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    );
  }

  it('表構造（caption / columnheader / cell）を保つ', () => {
    render(<Fixture />);
    const table = screen.getByRole('table', { name: 'データソース一覧' });
    expect(table).toBeInTheDocument();
    expect(screen.getAllByRole('columnheader')).toHaveLength(2);
    expect(screen.getByRole('cell', { name: '共有フォルダ' })).toBeInTheDocument();
  });

  it('見出しセルの scope は既定で col（省略できる API にしない）', () => {
    render(<Fixture />);
    for (const th of screen.getAllByRole('columnheader')) {
      expect(th).toHaveAttribute('scope', 'col');
    }
  });

  it('scope は行見出しへ上書きできる', () => {
    render(
      <Table>
        <TableBody>
          <TableRow>
            <TableHeaderCell scope="row">共有フォルダ</TableHeaderCell>
            <TableCell>同期済み</TableCell>
          </TableRow>
        </TableBody>
      </Table>,
    );
    expect(screen.getByRole('rowheader', { name: '共有フォルダ' })).toHaveAttribute('scope', 'row');
  });
});

describe('Tabs（SC-09 の 4 タブ）', () => {
  function Fixture() {
    return (
      <Tabs defaultValue="attrs">
        <TabsList aria-label="ABAC 設定">
          <TabsTrigger value="attrs">属性体系</TabsTrigger>
          <TabsTrigger value="tags">タグ辞書</TabsTrigger>
        </TabsList>
        <TabsContent value="attrs">属性体系の内容</TabsContent>
        <TabsContent value="tags">タグ辞書の内容</TabsContent>
      </Tabs>
    );
  }

  it('tab ロールと選択状態を持ち、選択に応じて内容が入れ替わる', async () => {
    render(<Fixture />);
    const attrs = screen.getByRole('tab', { name: '属性体系' });
    const tags = screen.getByRole('tab', { name: 'タグ辞書' });

    expect(attrs).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('属性体系の内容')).toBeInTheDocument();

    await userEvent.click(tags);
    expect(tags).toHaveAttribute('aria-selected', 'true');
    expect(attrs).toHaveAttribute('aria-selected', 'false');
    expect(screen.getByText('タグ辞書の内容')).toBeInTheDocument();
  });

  it('矢印キーでタブを移動できる（ロービングタブインデックス）', async () => {
    render(<Fixture />);
    // click で tablist へフォーカスを入れてから矢印で移動する（focus() を直接呼ぶと
    // Radix の内部状態更新が act の外で起き、警告になる）。
    await userEvent.click(screen.getByRole('tab', { name: '属性体系' }));
    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: 'タグ辞書' })).toHaveFocus();
  });

  it('選択状態は色以外の手掛かり（aria-selected と data-state）も持つ', async () => {
    render(<Fixture />);
    const tags = screen.getByRole('tab', { name: 'タグ辞書' });
    await userEvent.click(tags);
    expect(tags).toHaveAttribute('data-state', 'active');
  });
});
