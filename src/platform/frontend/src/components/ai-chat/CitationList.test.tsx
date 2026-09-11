import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { I18nProvider } from '@lingui/react';
import { i18n } from '@foundation/i18n';
import { CitationList } from './CitationList';
import type { AskCitation } from './citations';

// SC-01 / 05_screens §共通シェル（右レール AI チャット）/ 裁定 6（出典と本文の対応印）: 出典の一覧。
// 05_screens §SC-01「区別の表示方法」: アイコン ＋ ラベルを併用し、色だけで意味を持たせない（INDEX 決定 21）。

const cite = (number: number, documentId: string): AskCitation => ({
  number,
  documentId,
  documentTitle: `文書 ${number}`,
  chunkId: `c${number}`,
  score: 0.9,
  snippet: `抜粋 ${number}`,
});

function renderList(props: Partial<Parameters<typeof CitationList>[0]> = {}) {
  return render(
    <I18nProvider i18n={i18n}>
      <CitationList citations={[cite(1, 'a'), cite(2, 'b')]} citePrefix="p" {...props} />
    </I18nProvider>,
  );
}

describe('CitationList', () => {
  // 脚注番号と、本文の `[n]` が結ぶ先のアンカー ID。
  it('numbers each source and gives it the anchor id the answer links to', () => {
    renderList();
    const items = screen.getAllByRole('listitem');
    expect(items[0]).toHaveAttribute('id', 'p-cite-1');
    expect(items[0]).toHaveTextContent('[1]');
    expect(items[1]).toHaveAttribute('id', 'p-cite-2');
  });

  // 種別はラベル（文字）で示す。Wiki も組織文書であり、個人資料だけがラベルを変える。
  it('labels the kind in text (wiki is still an organisational document; personal differs)', () => {
    renderList({
      kindOf: (c) => (c.documentId === 'a' ? 'wiki' : 'personal'),
    });
    expect(screen.getByText('組織文書')).toBeInTheDocument();
    expect(screen.getByText('個人資料')).toBeInTheDocument();
  });

  // 行き先（typed route）は呼び出し側が渡す。省略時は文字だけで、リンクは張らない。
  it('renders plain titles unless the caller supplies links', () => {
    renderList();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText('文書 1')).toBeInTheDocument();
  });

  it('uses the caller-supplied title renderer', () => {
    renderList({ renderTitle: (c) => <a href={`/docs/${c.documentId}`}>{c.documentTitle}</a> });
    expect(screen.getByRole('link', { name: '文書 1' })).toHaveAttribute('href', '/docs/a');
  });
});
