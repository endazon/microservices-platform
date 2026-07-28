import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-08, FR-07, UC-02: 送信ペイロード（範囲指定）・結果/出典表示・空縮退（存在秘匿）・異常系を検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { AnalysisDashboardPage } from './AnalysisDashboardPage';

const ANSWER = {
  answer: '変更点は A と B です [1]。',
  citations: [
    {
      number: 1,
      documentId: 'd1',
      documentTitle: '経費規程 2025',
      chunkId: 'c1',
      sourceUri: 'https://wiki.example/expense-2025',
      score: 0.91,
      snippet: '第3条 …',
    },
  ],
  model: 'gpt-x',
  inputTokens: 120,
  outputTokens: 45,
  answerId: 'a1',
};

beforeEach(() => mocks.apiFetch.mockReset());

describe('AnalysisDashboardPage (SC-08)', () => {
  it('renders the answer and numbered citations on success', async () => {
    mocks.apiFetch.mockResolvedValue(ANSWER);
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), '経費規程の変更点');
    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    expect(await screen.findByText(/変更点は A と B です/)).toBeInTheDocument();
    const link = screen.getByRole('link', { name: '経費規程 2025' });
    expect(link).toHaveAttribute('href', 'https://wiki.example/expense-2025');
  });

  it('builds the request with instruction, taskType and range (query/topK/attributeFilters)', async () => {
    mocks.apiFetch.mockResolvedValue(ANSWER);
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), '比較して');
    await userEvent.selectOptions(screen.getByLabelText('タスク種別'), 'Compare');
    await userEvent.type(screen.getByLabelText('検索クエリ'), '経費');
    const topk = screen.getByLabelText('TopK（文脈上限 1〜50）');
    await userEvent.clear(topk);
    await userEvent.type(topk, '5');
    await userEvent.click(screen.getByRole('button', { name: '属性フィルタを追加' }));
    await userEvent.type(screen.getByLabelText('属性キー 1'), 'department');
    await userEvent.type(screen.getByLabelText('属性値 1'), 'sales, legal');

    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    await waitFor(() => expect(mocks.apiFetch).toHaveBeenCalled());
    const [path, req] = mocks.apiFetch.mock.calls[0];
    expect(path).toBe('/analysis/analyze');
    expect(req).toMatchObject({ method: 'POST' });
    expect(req.json).toEqual({
      instruction: '比較して',
      taskType: 'Compare',
      range: { query: '経費', attributeFilters: { department: ['sales', 'legal'] }, topK: 5 },
    });
  });

  it('shows a neutral message when the result is empty (existence hidden / narrowing to empty)', async () => {
    mocks.apiFetch.mockResolvedValue({ ...ANSWER, answer: '', citations: [] });
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), '権限外の範囲');
    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    expect(await screen.findByText('該当する情報が見つかりませんでした。')).toBeInTheDocument();
  });

  it('disables submit while instruction is empty', async () => {
    render(<AnalysisDashboardPage />);
    expect(screen.getByRole('button', { name: '分析を実行' })).toBeDisabled();
    await userEvent.type(screen.getByLabelText('分析内容（指示）'), 'x');
    expect(screen.getByRole('button', { name: '分析を実行' })).toBeEnabled();
  });

  it('shows an alert on server error', async () => {
    // async throw にして await 後にリジェクトさせる（eager-rejection の誤検知回避）。
    mocks.apiFetch.mockImplementationOnce(async () => {
      throw new ApiError('server', 'x', 500);
    });
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), 'エラー');
    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('実行に失敗');
  });

  // T-08: 403/404 は「拒否/不在」を区別せず空縮退と同じ中立表示にする（存在秘匿。IADR-0009）。
  it.each([
    ['forbidden', 403],
    ['notFound', 404],
  ] as const)('treats %s (%d) as a neutral empty state, not an alert', async (kind, status) => {
    mocks.apiFetch.mockImplementationOnce(async () => {
      throw new ApiError(kind, 'x', status);
    });
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), '権限外');
    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    expect(await screen.findByText('該当する情報が見つかりませんでした。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // T-15f, IADR-0111 (#403): 縮退応答（AI へ送信していない）は model が空で届く。
  // 「モデル: 」の後ろが空白のまま残らず、未送信であることが読み取れる表示にする。
  it('labels an empty model as not sent, and shows the model name otherwise', async () => {
    mocks.apiFetch.mockResolvedValue({
      ...ANSWER,
      answer: '機密区分により AI 送信を行いませんでした。',
      model: '',
      inputTokens: 0,
      outputTokens: 0,
    });
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), '機密文書の分析');
    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    expect(await screen.findByText(/モデル: 未使用（AI へ送信なし）/)).toBeInTheDocument();
    expect(screen.queryByText(/claude-opus-5/)).not.toBeInTheDocument();
  });

  it('shows the reported model name when the answer was generated', async () => {
    mocks.apiFetch.mockResolvedValue({ ...ANSWER, model: 'claude-sonnet-5' });
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), '経費規程の変更点');
    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    expect(await screen.findByText(/モデル: claude-sonnet-5/)).toBeInTheDocument();
  });

  // T-09: 明示的な topK=0 は既定 8 へ静かに戻さず下限 1 へクランプする。
  it('clamps an explicit topK of 0 to the lower bound (1), not the default', async () => {
    mocks.apiFetch.mockResolvedValue(ANSWER);
    render(<AnalysisDashboardPage />);

    await userEvent.type(screen.getByLabelText('分析内容（指示）'), '範囲確認');
    const topk = screen.getByLabelText('TopK（文脈上限 1〜50）');
    await userEvent.clear(topk);
    await userEvent.type(topk, '0');
    await userEvent.click(screen.getByRole('button', { name: '分析を実行' }));

    await waitFor(() => expect(mocks.apiFetch).toHaveBeenCalled());
    const [, req] = mocks.apiFetch.mock.calls[0];
    expect(req.json.range?.topK).toBe(1);
  });
});
