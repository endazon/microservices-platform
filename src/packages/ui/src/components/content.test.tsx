import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Kv, KvItem } from './Kv';
import { Note } from './Note';
import { Panel } from './Panel';
import { ProgressBar } from './ProgressBar';
import { Rule } from './Rule';
import { Stat } from './Stat';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの本文の語彙（.panel / .stat / .bar / .kv / .note / .hr）。

describe('Panel（本文の区画）', () => {
  it('見出しと中身を描き、見出しは既定で h2', () => {
    render(<Panel heading="統制の状態">中身</Panel>);
    expect(screen.getByRole('heading', { level: 2, name: '統制の状態' })).toBeInTheDocument();
    expect(screen.getByText('中身')).toBeInTheDocument();
  });

  it('見出しレベルは headingAs で変えられる（入れ子で階層が壊れないようにする）', () => {
    render(
      <Panel heading="内訳" headingAs="h3">
        中身
      </Panel>,
    );
    expect(screen.getByRole('heading', { level: 3, name: '内訳' })).toBeInTheDocument();
  });

  it('heading を渡さなければ見出しは出ない', () => {
    render(<Panel>中身</Panel>);
    expect(screen.queryByRole('heading')).toBeNull();
  });
});

describe('Stat（大きな数字）', () => {
  it('ラベル・値・補足を描く', () => {
    render(<Stat label="当日損益" value="+12,400" meta="前日比 +3.1%" />);
    expect(screen.getByText('当日損益')).toBeInTheDocument();
    expect(screen.getByText('+12,400')).toBeInTheDocument();
    expect(screen.getByText('前日比 +3.1%')).toBeInTheDocument();
  });

  it('🔴 tone は色だけを変える。意味はラベルと補足が担う（INDEX 決定 21）', () => {
    render(<Stat label="上限使用率" value="92%" meta="上限に近づいています" tone="warn" />);
    // 色を落としても「上限に近づいています」は読める。
    expect(screen.getByText('上限に近づいています')).toBeInTheDocument();
  });
});

describe('ProgressBar', () => {
  it('progressbar ロールと aria-* を持ち、割合を文字でも出す', () => {
    render(<ProgressBar label="上限使用率" value={45} />);
    const bar = screen.getByRole('progressbar', { name: '上限使用率' });
    expect(bar).toHaveAttribute('aria-valuenow', '45');
    expect(bar).toHaveAttribute('aria-valuemax', '100');
    expect(screen.getByText('45%')).toBeInTheDocument();
  });

  it('max を指定すると割合を換算する', () => {
    render(<ProgressBar label="消化量" value={25} max={50} />);
    expect(screen.getByRole('progressbar', { name: '消化量' })).toHaveAttribute(
      'aria-valuemax',
      '50',
    );
    expect(screen.getByText('50%')).toBeInTheDocument();
  });

  it('範囲外の値は丸める（棒が器からはみ出さない）', () => {
    const { rerender } = render(<ProgressBar label="使用率" value={140} />);
    expect(screen.getByText('100%')).toBeInTheDocument();
    rerender(<ProgressBar label="使用率" value={-20} />);
    expect(screen.getByText('0%')).toBeInTheDocument();
  });
});

describe('Kv / KvItem（ラベルと値の対応）', () => {
  it('dl / dt / dd で組む（ラベルと値の対応が支援技術へ伝わる）', () => {
    const { container } = render(
      <Kv columns={3}>
        <KvItem label="銘柄">7203</KvItem>
        <KvItem label="数量">100</KvItem>
      </Kv>,
    );
    expect(container.querySelector('dl')).not.toBeNull();
    expect(container.querySelectorAll('dt')).toHaveLength(2);
    expect(container.querySelectorAll('dd')).toHaveLength(2);
    expect(screen.getByText('銘柄')).toBeInTheDocument();
    expect(screen.getByText('7203')).toBeInTheDocument();
  });

  it('列数は静的なクラス名で与える（Tailwind が走査できる形にする）', () => {
    const { container } = render(
      <Kv columns={4}>
        <KvItem label="a">1</KvItem>
      </Kv>,
    );
    expect(container.querySelector('dl')?.className).toContain('grid-cols-4');
  });
});

describe('Note（常設の注記）', () => {
  it('role を持たない（画面を開くたびに読み上げられないようにする）', () => {
    const { container } = render(<Note>発注は取引時間内のみ受け付ける。</Note>);
    expect(container.querySelector('[role]')).toBeNull();
    expect(screen.getByText('発注は取引時間内のみ受け付ける。')).toBeInTheDocument();
  });
});

describe('Rule', () => {
  it('hr として描かれる', () => {
    render(<Rule />);
    expect(screen.getByRole('separator')).toBeInTheDocument();
  });
});
