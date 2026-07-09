import { useCallback, useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { apiFetch } from '@foundation/api/apiClient';

// SC-02, UC-01, FR-03/FR-05: 検索結果一覧画面。キーワード／意味検索の結果を一覧表示し、各件から
// SC-03（文書詳細）へ内部遷移する。データソースは BFF 集約（POST /bff/search）。ABAC はサーバ側で
// 適用され、権限外の文書は結果に現れない（deny-by-default → 空一覧。存在秘匿・IADR-0009）。
// SC-01（検索／AI質問）が出典を外部 URI で開くのに対し、本画面は文書詳細（SC-03）への内部導線を担う。

interface SearchResult {
  chunkId: string;
  documentId: string;
  documentTitle: string;
  text: string;
  score: number;
  markdownUri?: string | null;
  attributes?: Record<string, string>;
  tags?: string[];
}
interface SearchResponse {
  results: SearchResult[];
  totalHits: number;
  elapsedMs: number;
}

type Status = 'idle' | 'loading' | 'ok' | 'error';

export function SearchResultsPage() {
  const [params, setParams] = useSearchParams();
  const initialQuery = params.get('q') ?? '';
  const [input, setInput] = useState(initialQuery);
  const [status, setStatus] = useState<Status>('idle');
  const [response, setResponse] = useState<SearchResponse | null>(null);

  // FR-03: 検索を実行する。ABAC は BFF で適用されるため、クライアントはスコープを送らない。
  const runSearch = useCallback((query: string) => {
    const q = query.trim();
    if (!q) {
      setStatus('idle');
      setResponse(null);
      return;
    }
    setStatus('loading');
    apiFetch<SearchResponse>('/search', { method: 'POST', json: { query: q, topK: 20 } })
      .then((data) => {
        setResponse(data ?? { results: [], totalHits: 0, elapsedMs: 0 });
        setStatus('ok');
      })
      .catch(() => setStatus('error'));
  }, []);

  // 初期表示・URL の ?q= 変化（SC-01 等からのディープリンク・戻る操作）で検索する。
  const urlQuery = params.get('q') ?? '';
  useEffect(() => {
    if (urlQuery.trim()) {
      setInput(urlQuery);
      runSearch(urlQuery);
    }
  }, [urlQuery, runSearch]);

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    const q = input.trim();
    // 検索条件を URL に反映し、共有・戻る操作で再現できるようにする。
    setParams(q ? { q } : {});
    runSearch(q);
  }

  const results = response?.results ?? [];

  return (
    <section>
      <h1>検索結果一覧</h1>

      <form onSubmit={onSubmit} role="search">
        <label htmlFor="q">キーワード・意味検索</label>
        <br />
        <input
          id="q"
          type="text"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="例: 経費規程 出張"
          style={{ width: '100%', maxWidth: 640 }}
        />
        <div>
          <button type="submit" disabled={input.trim().length === 0}>
            検索する
          </button>
        </div>
      </form>

      {status === 'loading' && <p role="status">検索中…</p>}
      {status === 'error' && <p role="alert">検索に失敗しました。</p>}

      {status === 'ok' && (
        <section aria-label="検索結果" style={{ marginTop: '1rem' }}>
          {results.length === 0 ? (
            // deny-by-default: 権限外・0 件はいずれも中立に「見つからない」と表示する（存在秘匿）。
            <p>該当する文書が見つかりませんでした。</p>
          ) : (
            <>
              <p>
                <small>
                  {response?.totalHits ?? results.length} 件（表示 {results.length} 件）
                </small>
              </p>
              <ul style={{ listStyle: 'none', padding: 0 }}>
                {results.map((r) => (
                  <ResultItem key={r.chunkId} result={r} />
                ))}
              </ul>
            </>
          )}
        </section>
      )}
    </section>
  );
}

function ResultItem({ result }: { result: SearchResult }) {
  const attrs = Object.entries(result.attributes ?? {});
  return (
    <li style={{ border: '1px solid #eee', borderRadius: 6, padding: '0.5rem 0.75rem', margin: '0.5rem 0' }}>
      {/* SC-02 → SC-03: 文書詳細へ内部遷移する（ABAC はサーバ側で再適用）。 */}
      <Link to={`/documents/${result.documentId}`} style={{ fontWeight: 600 }}>
        {result.documentTitle}
      </Link>{' '}
      <small aria-label="スコア">score {result.score.toFixed(2)}</small>
      <p style={{ margin: '0.25rem 0', color: '#444' }}>{result.text}</p>
      {(attrs.length > 0 || (result.tags?.length ?? 0) > 0) && (
        <div>
          {attrs.map(([k, v]) => (
            <span
              key={k}
              style={{ display: 'inline-block', border: '1px solid #ccc', borderRadius: 4, padding: '0 0.4rem', marginRight: '0.3rem', fontSize: '0.85em' }}
            >
              {k}: {v}
            </span>
          ))}
          {result.tags?.map((t) => (
            <span key={t} style={{ marginRight: '0.3rem', fontSize: '0.85em', color: '#666' }}>
              #{t}
            </span>
          ))}
        </div>
      )}
    </li>
  );
}
