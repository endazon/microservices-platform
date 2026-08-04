import { useCallback, useEffect, useState } from 'react';
import { Link } from '@tanstack/react-router';
import { apiFetch } from '@foundation/api/apiClient';

// SC-06, UC-04, FR-01/FR-02: データソース管理画面。ソースの登録・一覧・同期状態・無効化を行う。
// データソースは文書 ABAC のスコープ対象ではなく運用資産のため、管理者・運用者に限定する
// （ルートは RequireRole で存在秘匿、サーバ側 /bff/datasources も同ロールに限定・IADR-0039）。
// 変換ジョブ（SC-07）への導線を持つ（取り込み→変換の運用フロー）。

interface DataSource {
  id: string;
  name: string;
  sourceType: string;
  connectionUri: string;
  status: string;
  lastSyncedAt?: string | null;
  defaultAttributes: Record<string, string>;
  createdAt: string;
}

// FR-01, UC-04: コネクタ種別（計画のデータソース種別に対応）。
const SOURCE_TYPES = ['filesystem', 'wiki', 'saas', 'db'] as const;
// FR-05, ADR-0004: 機密区分の許可値（AuthorizationService の属性辞書に準拠）。
const CONFIDENTIALITY = ['public', 'internal', 'confidential', 'restricted'] as const;

type ListStatus = 'loading' | 'ok' | 'error';

function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  const t = Date.parse(value);
  return Number.isNaN(t) ? value : new Date(t).toLocaleString();
}

export function DataSourceManagementPage() {
  const [status, setStatus] = useState<ListStatus>('loading');
  const [sources, setSources] = useState<DataSource[]>([]);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(() => {
    setStatus('loading');
    apiFetch<DataSource[]>('/datasources')
      .then((data) => {
        setSources(data ?? []);
        setStatus('ok');
      })
      .catch(() => setStatus('error'));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  async function onSync(id: string) {
    setNotice(null);
    try {
      await apiFetch(`/datasources/${id}/sync`, { method: 'POST' });
      setNotice('同期をトリガしました。');
      load();
    } catch {
      setNotice('同期のトリガに失敗しました。');
    }
  }

  async function onDisable(id: string) {
    setNotice(null);
    try {
      await apiFetch(`/datasources/${id}`, { method: 'DELETE' });
      setNotice('データソースを無効化しました。');
      load();
    } catch {
      setNotice('無効化に失敗しました。');
    }
  }

  return (
    <section>
      <h1>データソース管理</h1>
      <p>
        <Link to="/admin/conversions">変換ジョブを見る（SC-07）</Link>
      </p>

      <CreateForm
        onCreated={() => {
          setNotice('データソースを登録しました。');
          load();
        }}
      />

      {notice && <p role="status">{notice}</p>}

      <h2>登録済みデータソース</h2>
      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'error' && <p role="alert">データソースの取得に失敗しました。</p>}
      {status === 'ok' &&
        (sources.length === 0 ? (
          <p>データソースは登録されていません。</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>名前</th>
                <th>種別</th>
                <th>接続先</th>
                <th>状態</th>
                <th>機密区分</th>
                <th>最終同期</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {sources.map((ds) => (
                <tr key={ds.id}>
                  <td>{ds.name}</td>
                  <td>{ds.sourceType}</td>
                  <td>
                    <code>{ds.connectionUri}</code>
                  </td>
                  <td>{ds.status}</td>
                  <td>{ds.defaultAttributes?.confidentiality ?? '—'}</td>
                  <td>{formatDate(ds.lastSyncedAt)}</td>
                  <td>
                    <button type="button" onClick={() => void onSync(ds.id)}>
                      同期
                    </button>{' '}
                    {ds.status !== 'disabled' && (
                      <button type="button" onClick={() => void onDisable(ds.id)}>
                        無効化
                      </button>
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

// FR-01, UC-04: データソース登録フォーム。名前・接続先は必須。既定機密区分（ABAC 属性）を伴って登録する。
function CreateForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState('');
  const [sourceType, setSourceType] = useState<(typeof SOURCE_TYPES)[number]>('filesystem');
  const [connectionUri, setConnectionUri] = useState('');
  const [confidentiality, setConfidentiality] = useState<(typeof CONFIDENTIALITY)[number]>('internal');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const canSubmit = name.trim().length > 0 && connectionUri.trim().length > 0 && !submitting;

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    setSubmitting(true);
    setError(null);
    try {
      await apiFetch('/datasources', {
        method: 'POST',
        json: {
          name: name.trim(),
          sourceType,
          connectionUri: connectionUri.trim(),
          defaultAttributes: { confidentiality },
        },
      });
      setName('');
      setConnectionUri('');
      onCreated();
    } catch {
      setError('データソースの登録に失敗しました。');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={onSubmit} aria-label="データソース登録">
      <h2>データソースを登録</h2>
      <div>
        <label htmlFor="ds-name">名前（必須）</label>
        <br />
        <input id="ds-name" type="text" value={name} onChange={(e) => setName(e.target.value)} />
      </div>
      <div>
        <label htmlFor="ds-type">種別</label>
        <br />
        <select id="ds-type" value={sourceType} onChange={(e) => setSourceType(e.target.value as typeof sourceType)}>
          {SOURCE_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </div>
      <div>
        <label htmlFor="ds-uri">接続先 URI（必須）</label>
        <br />
        <input id="ds-uri" type="text" value={connectionUri} onChange={(e) => setConnectionUri(e.target.value)} />
      </div>
      <div>
        <label htmlFor="ds-conf">既定の機密区分</label>
        <br />
        <select
          id="ds-conf"
          value={confidentiality}
          onChange={(e) => setConfidentiality(e.target.value as typeof confidentiality)}
        >
          {CONFIDENTIALITY.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      </div>
      {error && <p role="alert">{error}</p>}
      <button type="submit" disabled={!canSubmit}>
        登録する
      </button>
    </form>
  );
}
