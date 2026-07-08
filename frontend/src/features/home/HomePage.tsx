import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// Issue #126: スケルトンの実例 feature。認証必須ルートから BFF を 1 本呼び、
// 疎結合（apiFetch 経由・実行時 config の接続先）で結果を描画する。SC-01..11 はこの形を踏襲する。
interface DashboardSummary {
  // BFF の /bff/dashboard/summary 契約（OpenAPI）に相当。骨組みでは存在すれば表示する。
  [key: string]: unknown;
}

type Status = 'loading' | 'ok' | 'notFound' | 'error';

export function HomePage() {
  const [status, setStatus] = useState<Status>('loading');
  const [summary, setSummary] = useState<DashboardSummary | null>(null);

  useEffect(() => {
    let active = true;
    apiFetch<DashboardSummary>('/dashboard/summary')
      .then((data) => {
        if (!active) return;
        setSummary(data);
        setStatus('ok');
      })
      .catch((e: unknown) => {
        if (!active) return;
        // IADR-0009: notFound は不在/秘匿を区別しない。
        setStatus(e instanceof ApiError && e.kind === 'notFound' ? 'notFound' : 'error');
      });
    return () => {
      active = false;
    };
  }, []);

  return (
    <section>
      <h1>ホーム</h1>
      <p>フロントエンド基盤（#126）のスケルトンです。SC-01〜11 は features として順次追加します。</p>
      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'ok' && (
        <pre aria-label="dashboard-summary">{JSON.stringify(summary, null, 2)}</pre>
      )}
      {status === 'notFound' && <p>ダッシュボード概要は利用できません。</p>}
      {status === 'error' && <p role="alert">概要の取得に失敗しました。</p>}
    </section>
  );
}
