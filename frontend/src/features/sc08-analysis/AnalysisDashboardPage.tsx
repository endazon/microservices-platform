import { useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// SC-08, UC-02, FR-07: AI分析ダッシュボード。データ範囲を指定して分析・比較・抽出を依頼し、
// 回答と番号付き出典を表示する。範囲は ABAC と narrowing-only で交差し権限を広げない（IADR-0005）。
// 権限外は空回答へ縮退し、権限の有無は開示しない（存在秘匿。UC-02 例外フロー）。

// BFF/OpenAPI 契約に対応する表示・送信用の型（camelCase）。
type TaskType = 'Analyze' | 'Compare' | 'Extract';

interface Citation {
  number: number;
  documentId: string;
  documentTitle: string;
  chunkId: string;
  sourceUri?: string | null;
  score: number;
  snippet: string;
}
interface AiAnswer {
  answer: string;
  citations: Citation[];
  model: string;
  inputTokens: number;
  outputTokens: number;
  answerId: string;
}
interface AnalysisRange {
  query?: string;
  attributeFilters?: Record<string, string[]>;
  topK?: number;
}
interface AnalysisTaskRequest {
  instruction: string;
  taskType: TaskType;
  range?: AnalysisRange;
}

interface FilterRow {
  key: string;
  values: string;
}

type Status = 'idle' | 'loading' | 'ok' | 'empty' | 'error';

const DEFAULT_TOPK = 8;
// バックエンド AnalysisPromptBuilder.MaxInstructionLength と揃える。超過は 400 になるためクライアントで予防する。
const MAX_INSTRUCTION_LENGTH = 2000;
const MIN_TOPK = 1;
const MAX_TOPK = 50;
const TASK_LABELS: Record<TaskType, string> = {
  Analyze: '分析',
  Compare: '比較',
  Extract: '抽出',
};

// 属性フィルタ行を {key: [values]} へ畳み込む。空 key・空値は除外し、無ければ undefined。
function rowsToFilters(rows: FilterRow[]): Record<string, string[]> | undefined {
  const out: Record<string, string[]> = {};
  for (const r of rows) {
    const key = r.key.trim();
    if (!key) continue;
    const values = r.values
      .split(',')
      .map((v) => v.trim())
      .filter(Boolean);
    if (values.length) out[key] = values;
  }
  return Object.keys(out).length ? out : undefined;
}

export function AnalysisDashboardPage() {
  const [instruction, setInstruction] = useState('');
  const [taskType, setTaskType] = useState<TaskType>('Analyze');
  const [query, setQuery] = useState('');
  const [topK, setTopK] = useState<number>(DEFAULT_TOPK);
  const [rows, setRows] = useState<FilterRow[]>([]);
  const [status, setStatus] = useState<Status>('idle');
  const [result, setResult] = useState<AiAnswer | null>(null);

  const trimmedInstruction = instruction.trim();
  // instruction は 1文字以上かつ上限内（400 をクライアントで予防）。
  const canSubmit =
    trimmedInstruction.length > 0 &&
    trimmedInstruction.length <= MAX_INSTRUCTION_LENGTH &&
    status !== 'loading';

  function buildRequest(): AnalysisTaskRequest {
    const range: AnalysisRange = {};
    const q = query.trim();
    if (q) range.query = q;
    const filters = rowsToFilters(rows);
    if (filters) range.attributeFilters = filters;
    // 明示的な 0・負値は下限 1 へクランプ（既定 8 へ静かに戻さない）。非数値のみ既定へフォールバック。
    const truncated = Math.trunc(topK);
    const clampedTopK = Number.isNaN(truncated)
      ? DEFAULT_TOPK
      : Math.min(Math.max(truncated, MIN_TOPK), MAX_TOPK);
    // range に他の指定があるか、TopK を既定から変えた場合のみ TopK を含める。
    if (range.query !== undefined || range.attributeFilters !== undefined || clampedTopK !== DEFAULT_TOPK) {
      range.topK = clampedTopK;
    }
    const req: AnalysisTaskRequest = { instruction: trimmedInstruction, taskType };
    if (Object.keys(range).length > 0) req.range = range;
    return req;
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    setStatus('loading');
    setResult(null);
    try {
      const answer = await apiFetch<AiAnswer>('/analysis/analyze', {
        method: 'POST',
        json: buildRequest(),
      });
      // 権限外は空回答へ縮退する（存在秘匿）。回答本文が無ければ中立表示にする。
      if (!answer || !answer.answer.trim()) {
        setResult(answer ?? null);
        setStatus('empty');
      } else {
        setResult(answer);
        setStatus('ok');
      }
    } catch (e) {
      // 403/404 は「拒否/不在」を区別せず空縮退と同じ中立表示にする（存在秘匿。IADR-0009・UC-02 例外フロー）。
      // 400/5xx/network は取得失敗として alert 表示にする。
      if (e instanceof ApiError && (e.kind === 'forbidden' || e.kind === 'notFound')) {
        setStatus('empty');
      } else {
        setStatus('error');
      }
    }
  }

  return (
    <section>
      <h1>AI分析ダッシュボード</h1>

      <form onSubmit={onSubmit}>
        <p>
          <label htmlFor="instruction">分析内容（指示）</label>
          <br />
          <textarea
            id="instruction"
            rows={3}
            maxLength={MAX_INSTRUCTION_LENGTH}
            style={{ width: '100%', maxWidth: 640 }}
            value={instruction}
            onChange={(e) => setInstruction(e.target.value)}
            placeholder="例: 2025 年度の経費規程の変更点を比較して"
          />
        </p>

        <p>
          <label htmlFor="taskType">タスク種別</label>{' '}
          <select
            id="taskType"
            value={taskType}
            onChange={(e) => setTaskType(e.target.value as TaskType)}
          >
            {(Object.keys(TASK_LABELS) as TaskType[]).map((t) => (
              <option key={t} value={t}>
                {TASK_LABELS[t]}
              </option>
            ))}
          </select>
        </p>

        <fieldset>
          <legend>対象範囲（任意）</legend>
          <p>
            <label htmlFor="range-query">検索クエリ</label>{' '}
            <input
              id="range-query"
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="省略時は指示を流用"
            />
          </p>
          <p>
            <label htmlFor="range-topk">TopK（文脈上限 1〜50）</label>{' '}
            <input
              id="range-topk"
              type="number"
              min={1}
              max={50}
              value={topK}
              onChange={(e) => setTopK(Number(e.target.value))}
              style={{ width: 80 }}
            />
          </p>

          <fieldset>
            <legend>属性フィルタ（narrowing-only）</legend>
            {rows.map((row, i) => (
              <p key={i} style={{ display: 'flex', gap: '0.5rem' }}>
                <input
                  aria-label={`属性キー ${i + 1}`}
                  type="text"
                  placeholder="キー（例: department）"
                  value={row.key}
                  onChange={(e) =>
                    setRows((rs) => rs.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))
                  }
                />
                <input
                  aria-label={`属性値 ${i + 1}`}
                  type="text"
                  placeholder="値（カンマ区切り 例: sales,legal）"
                  value={row.values}
                  onChange={(e) =>
                    setRows((rs) =>
                      rs.map((r, j) => (j === i ? { ...r, values: e.target.value } : r)),
                    )
                  }
                />
                <button type="button" onClick={() => setRows((rs) => rs.filter((_, j) => j !== i))}>
                  削除
                </button>
              </p>
            ))}
            <button type="button" onClick={() => setRows((rs) => [...rs, { key: '', values: '' }])}>
              属性フィルタを追加
            </button>
          </fieldset>
        </fieldset>

        <p>
          <button type="submit" disabled={!canSubmit}>
            分析を実行
          </button>
        </p>
      </form>

      {status === 'loading' && <p role="status">分析を実行中…</p>}
      {status === 'error' && <p role="alert">分析の実行に失敗しました。</p>}
      {status === 'empty' && <p>該当する情報が見つかりませんでした。</p>}

      {status === 'ok' && result && (
        <article aria-label="分析結果">
          <h2>回答</h2>
          <p style={{ whiteSpace: 'pre-wrap' }}>{result.answer}</p>

          {result.citations.length > 0 && (
            <>
              <h3>出典</h3>
              <ol>
                {result.citations.map((c) => (
                  <li key={c.chunkId}>
                    {c.sourceUri ? (
                      <a href={c.sourceUri} target="_blank" rel="noreferrer">
                        {c.documentTitle}
                      </a>
                    ) : (
                      <span>{c.documentTitle}</span>
                    )}{' '}
                    <small>（score: {c.score.toFixed(2)}）</small>
                    <div>
                      <small>{c.snippet}</small>
                    </div>
                  </li>
                ))}
              </ol>
            </>
          )}

          <p>
            <small>
              モデル: {result.model} / 入力 {result.inputTokens} ・出力 {result.outputTokens} トークン
            </small>
          </p>
        </article>
      )}
    </section>
  );
}
