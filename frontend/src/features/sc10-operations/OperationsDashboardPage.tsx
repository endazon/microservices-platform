import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import { appConfig } from '@foundation/config/runtimeConfig';
import { useHasAnyRole, PlatformRole } from '@foundation/auth/roles';

// SC-10, UC-05, FR-10: 運用ダッシュボード。BFF 集約（/bff/dashboard/summary）の要約を表示し、
// 詳細分析ツール（Grafana/Jaeger/Kiali）と構成ビューア（SC-11）への導線を提供する参照専用画面。
// データソースは AdminOnly（IADR-0011）。UI のロール判定は存在秘匿のためで、実効境界はサーバ側。

// BFF/OpenAPI 契約（DashboardSummaryDto）に対応する表示用の型（camelCase）。
interface UsagePoint {
  date: string;
  eventType: string;
  count: number;
}
interface SearchTrend {
  term: string;
  count: number;
}
interface FeedbackStats {
  up: number;
  down: number;
  total: number;
  satisfactionRate: number;
}
interface DashboardSummary {
  totalSearches: number;
  totalAnswers: number;
  usageTrend: UsagePoint[];
  topSearchTerms: SearchTrend[];
  quality: FeedbackStats;
}

type Status = 'loading' | 'ok' | 'forbidden' | 'notFound' | 'error';

const DAYS_OPTIONS = [7, 30, 90] as const;

export function OperationsDashboardPage() {
  const [days, setDays] = useState<number>(7);
  const [status, setStatus] = useState<Status>('loading');
  const [summary, setSummary] = useState<DashboardSummary | null>(null);

  // SC-11 導線は ConfigViewer 相当（管理者・運用者）にのみ出す（存在秘匿）。
  // 現状 /ops は AdminOnly のため Operator はここへ到達しないが、SC-11 の認可（管理者・運用者）と
  // 述語を共有する意図的な先取り。将来 SC-10 を運用者へ開放した際に導線を自動有効化するための冗長性
  // （デッドではあるが誤露出はしない。実効境界はサーバ側 AdminOnly）。詳細は SC-10 画面仕様書 §権限・表示条件。
  const canViewConfig = useHasAnyRole(PlatformRole.Admin, PlatformRole.Operator);
  const { grafanaUrl, jaegerUrl, kialiUrl } = appConfig().opsLinks;
  const tools = [
    { label: 'Grafana', url: grafanaUrl },
    { label: 'Jaeger', url: jaegerUrl },
    { label: 'Kiali', url: kialiUrl },
  ].filter((t): t is { label: string; url: string } => Boolean(t.url));

  useEffect(() => {
    let active = true;
    setStatus('loading');
    apiFetch<DashboardSummary>(`/dashboard/summary?days=${days}`)
      .then((data) => {
        if (!active) return;
        setSummary(data);
        setStatus('ok');
      })
      .catch((e: unknown) => {
        if (!active) return;
        // 権限・存在は中立に扱う（詳細を露出しない。IADR-0009）。
        if (e instanceof ApiError && e.kind === 'forbidden') setStatus('forbidden');
        else if (e instanceof ApiError && e.kind === 'notFound') setStatus('notFound');
        else setStatus('error');
      });
    return () => {
      active = false;
    };
  }, [days]);

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>運用ダッシュボード</h1>
        {canViewConfig && <Link to="/config">構成ビューア →</Link>}
      </div>

      <label>
        集計期間:{' '}
        <select
          aria-label="集計期間"
          value={days}
          onChange={(e) => setDays(Number(e.target.value))}
        >
          {DAYS_OPTIONS.map((d) => (
            <option key={d} value={d}>
              直近 {d} 日
            </option>
          ))}
        </select>
      </label>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'forbidden' && <p>このダッシュボードを表示する権限がありません。</p>}
      {status === 'notFound' && <p>ダッシュボードは利用できません。</p>}
      {status === 'error' && <p role="alert">概要の取得に失敗しました。</p>}

      {status === 'ok' && summary && (
        <>
          <div style={{ display: 'flex', gap: '1.5rem', margin: '1rem 0' }}>
            <SummaryCard label="検索総数" value={summary.totalSearches} />
            <SummaryCard label="回答総数" value={summary.totalAnswers} />
            <SummaryCard
              label="満足率"
              value={`${Math.round(summary.quality.satisfactionRate * 100)}%`}
            />
          </div>

          <div style={{ display: 'flex', gap: '2rem', flexWrap: 'wrap' }}>
            <section aria-label="利用状況">
              <h2>利用状況（日次）</h2>
              {summary.usageTrend.length === 0 ? (
                <p>期間内の利用はありません。</p>
              ) : (
                <ul>
                  {summary.usageTrend.map((p, i) => (
                    <li key={`${p.date}-${p.eventType}-${i}`}>
                      {p.date} / {p.eventType}: {p.count}
                    </li>
                  ))}
                </ul>
              )}
            </section>

            <section aria-label="検索傾向">
              <h2>検索傾向（上位語）</h2>
              {summary.topSearchTerms.length === 0 ? (
                <p>検索傾向はまだありません。</p>
              ) : (
                <ol>
                  {summary.topSearchTerms.map((t) => (
                    <li key={t.term}>
                      {t.term}: {t.count}
                    </li>
                  ))}
                </ol>
              )}
            </section>
          </div>
        </>
      )}

      <section aria-label="詳細ツール" style={{ marginTop: '1.5rem' }}>
        <h2>詳細ツール</h2>
        {tools.length === 0 ? (
          <p>外部ツールの導線は未設定です。</p>
        ) : (
          <ul style={{ display: 'flex', gap: '1rem', listStyle: 'none', padding: 0 }}>
            {tools.map((t) => (
              <li key={t.label}>
                <a href={t.url} target="_blank" rel="noreferrer">
                  {t.label}
                </a>
              </li>
            ))}
          </ul>
        )}
      </section>
    </section>
  );
}

function SummaryCard({ label, value }: { label: string; value: number | string }) {
  return (
    <div style={{ border: '1px solid #ddd', borderRadius: 8, padding: '0.75rem 1rem', minWidth: 120 }}>
      <div style={{ fontSize: '0.85rem', color: '#666' }}>{label}</div>
      <div style={{ fontSize: '1.5rem', fontWeight: 600 }}>{value}</div>
    </div>
  );
}
