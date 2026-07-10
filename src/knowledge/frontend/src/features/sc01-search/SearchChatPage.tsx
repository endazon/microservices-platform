import { useRef, useState } from 'react';
import { apiFetch, apiStream } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// SC-01, UC-01, FR-03/FR-04: 検索／チャット質問画面（本システムの主入口）。
// 1 つの入力から (1) 横断検索（/bff/search）と (2) 根拠付き AI 回答（/bff/analysis/ask/stream の
// 真の SSE ストリーミング・出典併記）を行い、👍/👎（/bff/feedback）を送る。出典クリックで出典元へ遷移。

interface Citation {
  number: number;
  documentId: string;
  documentTitle: string;
  chunkId: string;
  sourceUri?: string | null;
  score: number;
  snippet: string;
}
interface SearchResult {
  chunkId: string;
  documentId: string;
  documentTitle: string;
  text: string;
  score: number;
  markdownUri?: string | null;
}
interface SearchResponse {
  results: SearchResult[];
  totalHits: number;
  elapsedMs: number;
}
interface DonePayload {
  answerId: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
}

type AnswerStatus = 'idle' | 'streaming' | 'done' | 'error';

export function SearchChatPage() {
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState('');
  const [citations, setCitations] = useState<Citation[]>([]);
  const [answerStatus, setAnswerStatus] = useState<AnswerStatus>('idle');
  const [answerId, setAnswerId] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<'up' | 'down' | null>(null);

  const [results, setResults] = useState<SearchResult[]>([]);
  const [searchStatus, setSearchStatus] = useState<'idle' | 'loading' | 'ok' | 'error'>('idle');

  const abortRef = useRef<AbortController | null>(null);
  const canSubmit = question.trim().length > 0 && answerStatus !== 'streaming';

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    const q = question.trim();
    if (!q || answerStatus === 'streaming') return;

    // 前のストリームを中断してリセットする。
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setAnswer('');
    setCitations([]);
    setAnswerId(null);
    setFeedback(null);
    setAnswerStatus('streaming');
    setResults([]);
    setSearchStatus('loading');

    // (1) 横断検索（AI 回答と独立に取得）。
    apiFetch<SearchResponse>('/search', { method: 'POST', json: { query: q, topK: 10 } })
      .then((data) => {
        setResults(data?.results ?? []);
        setSearchStatus('ok');
      })
      .catch(() => setSearchStatus('error'));

    // (2) AI 回答の SSE ストリーミング（出典先行 → 本文トークン → done）。
    try {
      await apiStream(
        '/analysis/ask/stream',
        { json: { question: q } },
        (ev) => {
          if (ev.event === 'citations') {
            const parsed = JSON.parse(ev.data) as { citations: Citation[] };
            setCitations(parsed.citations ?? []);
          } else if (ev.event === 'token') {
            const parsed = JSON.parse(ev.data) as { text: string };
            setAnswer((a) => a + parsed.text);
          } else if (ev.event === 'done') {
            const parsed = JSON.parse(ev.data) as DonePayload;
            setAnswerId(parsed.answerId);
            setAnswerStatus('done');
          } else if (ev.event === 'error') {
            setAnswerStatus('error');
          }
        },
        controller.signal,
      );
      // done イベントが来なかった場合も終了状態にする。
      setAnswerStatus((s) => (s === 'streaming' ? 'done' : s));
    } catch (err) {
      // 中断（新しい質問・アンマウント）は無視。それ以外は失敗表示。
      if (!(err instanceof DOMException && err.name === 'AbortError')) {
        setAnswerStatus('error');
      }
    }
  }

  async function sendFeedback(rating: 'up' | 'down') {
    if (!answerId) return;
    setFeedback(rating); // 楽観的に反映
    try {
      await apiFetch('/feedback', { method: 'POST', json: { answerId, rating, question } });
    } catch (e) {
      if (e instanceof ApiError) setFeedback(null); // 失敗時は取り消す
    }
  }

  return (
    <section>
      <h1>検索 / AI質問</h1>

      <form onSubmit={onSubmit}>
        <label htmlFor="q">質問・キーワード</label>
        <br />
        <input
          id="q"
          type="text"
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          placeholder="例: 2025 年度の経費規程の変更点は？"
          style={{ width: '100%', maxWidth: 640 }}
        />
        <div>
          <button type="submit" disabled={!canSubmit}>
            質問する
          </button>
        </div>
      </form>

      {/* AI 回答（ストリーミング） */}
      {answerStatus !== 'idle' && (
        <article aria-label="AI回答" style={{ marginTop: '1rem' }}>
          <h2>回答</h2>
          {answerStatus === 'streaming' && <span role="status">回答を生成中…</span>}
          <p style={{ whiteSpace: 'pre-wrap' }}>{answer}</p>

          {answerStatus === 'error' && <p role="alert">回答の生成に失敗しました。</p>}

          {citations.length > 0 && (
            <>
              <h3>出典</h3>
              <ol>
                {citations.map((c) => (
                  <li key={c.chunkId}>
                    {c.sourceUri ? (
                      // 出典クリックで出典元（Wiki=SC-04 等）へ遷移する。SC-03 文書詳細は #129 実装後に内部遷移へ。
                      <a href={c.sourceUri} target="_blank" rel="noreferrer">
                        {c.documentTitle}
                      </a>
                    ) : (
                      <span>{c.documentTitle}</span>
                    )}{' '}
                    <small>（{c.snippet}）</small>
                  </li>
                ))}
              </ol>
            </>
          )}

          {answerStatus === 'done' && answerId && (
            <div aria-label="フィードバック">
              <button type="button" aria-pressed={feedback === 'up'} onClick={() => void sendFeedback('up')}>
                👍
              </button>{' '}
              <button type="button" aria-pressed={feedback === 'down'} onClick={() => void sendFeedback('down')}>
                👎
              </button>
              {feedback && <span> フィードバックを送信しました。</span>}
            </div>
          )}
        </article>
      )}

      {/* 横断検索結果 */}
      {searchStatus !== 'idle' && (
        <section aria-label="検索結果" style={{ marginTop: '1.5rem' }}>
          <h2>検索結果</h2>
          {searchStatus === 'loading' && <p role="status">検索中…</p>}
          {searchStatus === 'error' && <p role="alert">検索に失敗しました。</p>}
          {searchStatus === 'ok' &&
            (results.length === 0 ? (
              <p>該当する文書が見つかりませんでした。</p>
            ) : (
              <ul>
                {results.map((r) => (
                  <li key={r.chunkId}>
                    {r.markdownUri ? (
                      <a href={r.markdownUri} target="_blank" rel="noreferrer">
                        {r.documentTitle}
                      </a>
                    ) : (
                      <span>{r.documentTitle}</span>
                    )}{' '}
                    <small>{r.text}</small>
                  </li>
                ))}
              </ul>
            ))}
        </section>
      )}
    </section>
  );
}
