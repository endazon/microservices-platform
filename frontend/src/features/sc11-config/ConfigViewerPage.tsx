import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// SC-11, FR-15, ADR-0018: 構成ビューア。実効構成（構成バージョン・パイプライン段・イベント接続・
// ポート選択・コネクタ）を参照専用で可視化する。データソースは /bff/admin/config（ConfigViewer,
// 404 秘匿）。可視化はグラフ描画ライブラリを使わず CSS チェーン＋表で表現する（IADR-0035）。
// #137: 実効構成の表示。ドリフト（#138）・履歴（#139）は後続。

interface ConfigVersion {
  gitCommit?: string | null;
  appliedAt?: string | null;
  appliedBy?: string | null;
}
interface PipelineStage {
  name: string;
  service: string;
  consumer: string;
  input: string;
  outputs: string[];
  enabled: boolean;
}
interface EventBinding {
  event: string;
  publishers: string[];
  subscribers: string[];
}
interface PortSelection {
  port: string;
  implementation: string;
  target?: string | null;
}
interface Connector {
  name: string;
  enabled: boolean;
}
export interface EffectiveConfig {
  version: ConfigVersion;
  pipeline: PipelineStage[];
  eventBindings: EventBinding[];
  ports: PortSelection[];
  connectors: Connector[];
}

type Status = 'loading' | 'ok' | 'notFound' | 'error';

export function ConfigViewerPage() {
  const [status, setStatus] = useState<Status>('loading');
  const [config, setConfig] = useState<EffectiveConfig | null>(null);

  useEffect(() => {
    let active = true;
    apiFetch<EffectiveConfig>('/admin/config')
      .then((data) => {
        if (!active) return;
        setConfig(data);
        setStatus('ok');
      })
      .catch((e: unknown) => {
        if (!active) return;
        // 404 は不在/秘匿を区別しない（IADR-0009）。
        setStatus(e instanceof ApiError && e.kind === 'notFound' ? 'notFound' : 'error');
      });
    return () => {
      active = false;
    };
  }, []);

  return (
    <section>
      <h1>構成ビューア</h1>
      <p>現在有効なシステム構成（実効構成）を表示します（参照専用）。</p>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>構成情報は利用できません。</p>}
      {status === 'error' && <p role="alert">構成情報の取得に失敗しました。</p>}

      {status === 'ok' && config && (
        <>
          <ConfigVersionHeader version={config.version} />
          <PipelineView stages={config.pipeline} />
          <EventBindingsView bindings={config.eventBindings} />
          <PortsView ports={config.ports} />
          <ConnectorsView connectors={config.connectors} />
        </>
      )}
    </section>
  );
}

function ConfigVersionHeader({ version }: { version: ConfigVersion }) {
  const { gitCommit, appliedAt, appliedBy } = version;
  const short = gitCommit ? gitCommit.slice(0, 7) : '—';
  return (
    <div
      aria-label="構成バージョン"
      style={{ border: '1px solid #ddd', borderRadius: 8, padding: '0.5rem 0.75rem', margin: '0.75rem 0' }}
    >
      <strong>構成バージョン:</strong> <code>{short}</code>{' '}
      <span>／ 適用日時: {appliedAt ?? '—'}</span> <span>／ 適用者: {appliedBy ?? '—'}</span>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <details open style={{ margin: '0.75rem 0' }}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>{title}</summary>
      <div style={{ marginTop: '0.5rem' }}>{children}</div>
    </details>
  );
}

// パイプライン段: consumer → [outputs] の縦チェーン（IADR-0035）。無効段はグレーアウト。
function PipelineView({ stages }: { stages: PipelineStage[] }) {
  return (
    <Section title={`パイプライン段（${stages.length}）`}>
      {stages.length === 0 ? (
        <p>段は登録されていません。</p>
      ) : (
        <ol aria-label="パイプライン段" style={{ listStyle: 'none', padding: 0 }}>
          {stages.map((s) => (
            <li
              key={s.name}
              style={{
                border: '1px solid #ccc',
                borderRadius: 6,
                padding: '0.5rem 0.75rem',
                margin: '0.4rem 0',
                opacity: s.enabled ? 1 : 0.5,
              }}
            >
              <div>
                <strong>{s.name}</strong> <small>（{s.service}）</small>
                {!s.enabled && <span> — 無効</span>}
              </div>
              <div>
                <small>
                  consumer: {s.consumer}｜{s.input} → {s.outputs.length ? s.outputs.join(', ') : '（終端）'}
                </small>
              </div>
            </li>
          ))}
        </ol>
      )}
    </Section>
  );
}

function EventBindingsView({ bindings }: { bindings: EventBinding[] }) {
  return (
    <Section title={`イベント接続（${bindings.length}）`}>
      {bindings.length === 0 ? (
        <p>イベント接続はありません。</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>イベント</th>
              <th>発行者</th>
              <th>購読者</th>
            </tr>
          </thead>
          <tbody>
            {bindings.map((b) => (
              <tr key={b.event}>
                <td>{b.event}</td>
                <td>{b.publishers.join(', ') || '—'}</td>
                <td>{b.subscribers.join(', ') || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Section>
  );
}

function PortsView({ ports }: { ports: PortSelection[] }) {
  return (
    <Section title={`ポート実装選択（${ports.length}）`}>
      {ports.length === 0 ? (
        <p>ポートはありません。</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>ポート</th>
              <th>実装</th>
              <th>接続先</th>
            </tr>
          </thead>
          <tbody>
            {ports.map((p) => (
              <tr key={p.port}>
                <td>{p.port}</td>
                <td>{p.implementation}</td>
                <td>{p.target ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Section>
  );
}

function ConnectorsView({ connectors }: { connectors: Connector[] }) {
  return (
    <Section title={`コネクタ（${connectors.length}）`}>
      {connectors.length === 0 ? (
        <p>コネクタはありません。</p>
      ) : (
        <ul aria-label="コネクタ一覧">
          {connectors.map((c) => (
            <li key={c.name}>
              {c.name}: {c.enabled ? '有効' : '無効'}
            </li>
          ))}
        </ul>
      )}
    </Section>
  );
}
