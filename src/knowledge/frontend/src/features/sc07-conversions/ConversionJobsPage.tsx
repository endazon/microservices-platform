import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { apiFetch } from '@foundation/api/apiClient';

// SC-07, UC-06, FR-12: 変換ジョブ画面。変換状況・失敗ジョブの一覧を表示し、失敗ジョブの人手補正（再変換）を
// 行う。データソースは BFF 集約（/bff/conversion/jobs）。運用系のため platform-admin/operator 限定
// （ルートは RequireRole、サーバ側 BFF も同ロール）。SC-06（データソース管理）からの遷移先。

interface ConversionJob {
  id: string;
  sourceId: string;
  sourceType: string;
  originalPath: string;
  status: string;
  error?: string | null;
  documentId?: string | null;
  markdownUri?: string | null;
  attempts: number;
  createdAt: string;
  updatedAt: string;
}

// 状況フィルタ（空＝すべて）。
const FILTERS = ['', 'failed', 'processing', 'queued', 'succeeded'] as const;
const FILTER_LABELS: Record<string, string> = {
  '': 'すべて',
  failed: '失敗',
  processing: '処理中',
  queued: '待機',
  succeeded: '成功',
};

function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  const t = Date.parse(value);
  return Number.isNaN(t) ? value : new Date(t).toLocaleString();
}

export function ConversionJobsPage() {
  const [jobs, setJobs] = useState<ConversionJob[]>([]);
  const [status, setStatus] = useState<'loading' | 'ok' | 'error'>('loading');
  const [filter, setFilter] = useState<(typeof FILTERS)[number]>('');
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback((f: string) => {
    setStatus('loading');
    const path = f ? `/conversion/jobs?status=${encodeURIComponent(f)}` : '/conversion/jobs';
    apiFetch<ConversionJob[]>(path)
      .then((data) => {
        setJobs(data ?? []);
        setStatus('ok');
      })
      .catch(() => setStatus('error'));
  }, []);

  useEffect(() => {
    load(filter);
  }, [load, filter]);

  async function onRetry(id: string) {
    setNotice(null);
    try {
      await apiFetch(`/conversion/jobs/${id}/retry`, { method: 'POST' });
      setNotice('再変換をトリガしました。');
      load(filter);
    } catch {
      setNotice('再変換のトリガに失敗しました。');
    }
  }

  return (
    <section>
      <h1>変換ジョブ</h1>
      <p>
        <Link to="/datasources">データソース管理へ戻る（SC-06）</Link>
      </p>

      <div>
        <label htmlFor="job-filter">状況で絞り込み</label>{' '}
        <select id="job-filter" value={filter} onChange={(e) => setFilter(e.target.value as typeof filter)}>
          {FILTERS.map((f) => (
            <option key={f} value={f}>
              {FILTER_LABELS[f]}
            </option>
          ))}
        </select>
      </div>

      {notice && <p role="status">{notice}</p>}

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'error' && <p role="alert">変換ジョブの取得に失敗しました。</p>}
      {status === 'ok' &&
        (jobs.length === 0 ? (
          <p>該当する変換ジョブはありません。</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>原本</th>
                <th>種別</th>
                <th>状況</th>
                <th>試行</th>
                <th>エラー</th>
                <th>更新</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id}>
                  <td>{j.originalPath}</td>
                  <td>{j.sourceType}</td>
                  <td>{FILTER_LABELS[j.status] ?? j.status}</td>
                  <td>{j.attempts}</td>
                  <td>{j.error ?? '—'}</td>
                  <td>{formatDate(j.updatedAt)}</td>
                  <td>
                    {j.status === 'failed' ? (
                      <button type="button" onClick={() => void onRetry(j.id)}>
                        再変換
                      </button>
                    ) : j.status === 'succeeded' && j.documentId ? (
                      // 成功した変換は生成文書（SC-03）へ遷移できる。
                      <Link to={`/documents/${j.documentId}`}>文書を見る</Link>
                    ) : (
                      '—'
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
    </section>
  );
}
