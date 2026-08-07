import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Input } from './Input';
import { Textarea } from './Textarea';
import { Select } from './Select';
import { Label } from './Label';

// ADR-0031 / IADR-0125 決定 1: 入力系プリミティブ（SC-01 質問入力・SC-02 検索ボックス・
// SC-05 タイトル / 機密区分・SC-08 分析内容・SC-09 対象属性）の回帰。

describe('Input', () => {
  it('入力できる（制御されない素の input として振る舞う）', async () => {
    render(<Input aria-label="質問" placeholder="キーワード" />);
    const input = screen.getByLabelText('質問');
    await userEvent.type(input, '経費精算');
    expect(input).toHaveValue('経費精算');
  });

  it('invalid のとき aria-invalid が立つ（見た目と支援技術への通知を揃える）', () => {
    render(<Input aria-label="タイトル" invalid />);
    expect(screen.getByLabelText('タイトル')).toHaveAttribute('aria-invalid', 'true');
  });

  it('既定では aria-invalid を立てない', () => {
    render(<Input aria-label="タイトル" />);
    expect(screen.getByLabelText('タイトル')).not.toHaveAttribute('aria-invalid', 'true');
  });

  it('呼び出し側の className が cn() で合成される', () => {
    render(<Input aria-label="タイトル" className="mt-4" />);
    expect(screen.getByLabelText('タイトル')).toHaveClass('mt-4');
  });
});

describe('Textarea', () => {
  it('複数行入力の既定行数を持つ（SC-08 の分析内容）', () => {
    render(<Textarea aria-label="分析内容" />);
    expect(screen.getByLabelText('分析内容')).toHaveAttribute('rows', '4');
  });

  it('rows は呼び出し側が上書きできる', () => {
    render(<Textarea aria-label="分析内容" rows={10} />);
    expect(screen.getByLabelText('分析内容')).toHaveAttribute('rows', '10');
  });

  it('invalid のとき aria-invalid が立つ', () => {
    render(<Textarea aria-label="分析内容" invalid />);
    expect(screen.getByLabelText('分析内容')).toHaveAttribute('aria-invalid', 'true');
  });
});

describe('Select（ネイティブ select。IADR-0125 決定 1）', () => {
  const options = (
    <>
      <option value="public">公開</option>
      <option value="internal">社内限</option>
    </>
  );

  it('ネイティブの combobox として公開され、選択できる', async () => {
    render(
      <Select aria-label="機密区分" defaultValue="public">
        {options}
      </Select>,
    );
    const select = screen.getByLabelText('機密区分');
    // ポータル描画のカスタムリストボックスではなく素の <select> であることを固定する。
    expect(select.tagName).toBe('SELECT');
    await userEvent.selectOptions(select, 'internal');
    expect(select).toHaveValue('internal');
  });

  it('invalid のとき aria-invalid が立つ', () => {
    render(
      <Select aria-label="機密区分" invalid>
        {options}
      </Select>,
    );
    expect(screen.getByLabelText('機密区分')).toHaveAttribute('aria-invalid', 'true');
  });
});

describe('Label（必須の印は読み上げ文言なしでは出せない。INDEX 決定 21）', () => {
  it('htmlFor で入力と関連付く', () => {
    render(
      <>
        <Label htmlFor="title">タイトル</Label>
        <Input id="title" />
      </>,
    );
    expect(screen.getByLabelText('タイトル')).toBeInTheDocument();
  });

  it('requiredHint を渡すと印（装飾）と読み上げ文言の両方が出る', () => {
    render(<Label requiredHint="（必須）">タイトル</Label>);
    expect(screen.getByText('（必須）')).toBeInTheDocument();
    // 印そのものは装飾であり、意味は読み上げ文言が担う。
    const marker = screen.getByText('*');
    expect(marker).toHaveAttribute('aria-hidden', 'true');
  });

  it('requiredHint が無ければ印も出ない（記号だけの必須表現を作らせない）', () => {
    render(<Label>タイトル</Label>);
    expect(screen.queryByText('*')).toBeNull();
  });
});
