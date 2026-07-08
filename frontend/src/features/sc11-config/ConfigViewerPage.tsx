import type { ReactNode } from 'react';
import { useEffect, useMemo, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// SC-11, FR-15, ADR-0018: 構成ビューア。実効構成（構成バージョン・パイプライン段・イベント接続・
// ポート選択・コネクタ）を参照専用で可視化する。データソースは /bff/admin/config（ConfigViewer,
// 404 秘匿）。可視化はグラフ描画ライブラリを使わず CSS チェーン＋表で表現する（IADR-0036）。
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

// #138: ドリフト（宣言 Git との差分）。判定は API 側（DriftDetector）が行い、本画面は結果表示のみ。
interface DriftFinding {
  kind: string;
  severity: string;
  target: string;
  detail: string;
}
interface DriftReport {
  hasDrift: boolean;
  checkedAt: string;
  findings: DriftFinding[];
}

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type DriftStatus = 'loading' | 'ok' | 'unavailable';

export function ConfigViewerPage() {
  const [status, setStatus] = useState<Status>('loading');
  const [config, setConfig] = useState<EffectiveConfig | null>(null);
  const [driftStatus, setDriftStatus] = useState<DriftStatus>('loading');
  const [drift, setDrift] = useState<DriftReport | null>(null);

  // #138: ドリフト対象（finding.target）の集合。実効構成側（段）の強調判定に用いる。
  // DriftDetector（Shared.Infrastructure）は Target に常に段名（decl.Name/実効段名）のみを返し、
  // イベント名・ポート名は返さない（CLAUDE.md「起こり得ないケースへの防御的実装を避ける」）ため、
  // 強調表示はパイプライン段のみを対象とする。
  const driftTargets = useMemo(
    () => new Set((drift?.findings ?? []).map((f) => f.target)),
    [drift],
  );

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
    // #138: ドリフトは独立に取得する（構成表示と疎結合）。取得不能時はその領域のみ縮退。
    apiFetch<DriftReport>('/admin/config/drift')
      .then((data) => {
        if (!active) return;
        setDrift(data);
        setDriftStatus('ok');
      })
      .catch(() => {
        if (!active) return;
        setDriftStatus('unavailable');
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
          <DriftView status={driftStatus} report={drift} />
          {/* #138: §(1) 実効構成側にもドリフトを強調する。finding.target と一致する段に警告色を付け、
              (2) のドリフト明細（#drift-section）へリンクする（IADR-0036・SC-11 §(1)）。イベント接続・
              ポートは DriftDetector の対象外（Target は常に段名）のため強調表示の対象としない。 */}
          <PipelineView stages={config.pipeline} driftTargets={driftTargets} />
          <EventBindingsView bindings={config.eventBindings} />
          <PortsView ports={config.ports} />
          <ConnectorsView connectors={config.connectors} />
        </>
      )}
    </section>
  );
}

// ISO 8601（DateTimeOffset 由来）をロケール表記に整形する。解釈できない値はそのまま表示する。
function formatAppliedAt(value: string | null | undefined): string {
  if (!value) return '—';
  const t = Date.parse(value);
  return Number.isNaN(t) ? value : new Date(t).toLocaleString();
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
      <span>／ 適用日時: {formatAppliedAt(appliedAt)}</span> <span>／ 適用者: {appliedBy ?? '—'}</span>
    </div>
  );
}

// #138: 深刻度 → 強調色。severity は DriftDetector が返す Warning / Info の2値（IADR-0029）。
// 大文字小文字を無視して照合し、未知の深刻度は中立色にフォールバックする（種別同様、値域が
// 増えても UI 改修は不要）。実在しない深刻度への分岐は持たない（防御的実装を避ける）。
function severityColor(severity: string): string {
  switch (severity.toLowerCase()) {
    case 'warning':
      return '#e67e22';
    case 'info':
    default:
      return '#7f8c8d';
  }
}

// #138: ドリフト一覧・強調表示。0 件は「OK」を明示（検出済みを日時とともに示す）。
function DriftView({ status, report }: { status: DriftStatus; report: DriftReport | null }) {
  // API 側（ConfigInspectionService.GetDriftAsync）は HasDrift = findings.Count > 0 で構築するため、
  // 件数から一意に決まる（両者が乖離するケースは無い）。
  const count = report?.findings.length ?? 0;
  const hasDrift = count > 0;
  const badge = status !== 'ok' ? '—' : hasDrift ? `ドリフト ${count} 件` : 'OK';

  return (
    <Section title={`宣言との差分（ドリフト）: ${badge}`} id="drift-section">
      {status === 'loading' && <p role="status">ドリフトを確認中…</p>}
      {status === 'unavailable' && <p>ドリフト情報は利用できません。</p>}
      {status === 'ok' && report && !hasDrift && (
        <p>ドリフトなし（OK）。<small>確認時刻: {formatAppliedAt(report.checkedAt)}</small></p>
      )}
      {status === 'ok' && report && hasDrift && (
        <>
          <p>
            <small>確認時刻: {formatAppliedAt(report.checkedAt)}</small>
          </p>
          <table aria-label="ドリフト明細">
            <thead>
              <tr>
                <th>種別</th>
                <th>深刻度</th>
                <th>対象</th>
                <th>説明</th>
              </tr>
            </thead>
            <tbody>
              {report.findings.map((f, i) => (
                <tr key={`${f.kind}-${f.target}-${i}`}>
                  <td>{f.kind}</td>
                  <td style={{ color: severityColor(f.severity), fontWeight: 600 }}>{f.severity}</td>
                  <td>{f.target}</td>
                  <td>{f.detail}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </Section>
  );
}

function Section({ title, children, id }: { title: string; children: ReactNode; id?: string }) {
  return (
    <details id={id} open style={{ margin: '0.75rem 0' }}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>{title}</summary>
      <div style={{ marginTop: '0.5rem' }}>{children}</div>
    </details>
  );
}

// #138: ドリフト対象の項目に付ける警告マーク（(2) のドリフト明細 #drift-section へリンクする）。
function DriftMark() {
  return (
    <a
      href="#drift-section"
      title="この項目は宣言とドリフトしています"
      style={{ color: '#c0392b', marginLeft: '0.5rem', fontWeight: 600 }}
    >
      ⚠ ドリフト
    </a>
  );
}

// #138: ドリフト対象の行に付ける強調スタイル（警告色の枠・背景）。
function driftStyle(drifted: boolean): React.CSSProperties {
  return drifted ? { borderColor: '#e67e22', background: '#fdf2e6' } : {};
}

// パイプライン段: consumer → [outputs] の縦チェーン（IADR-0036）。無効段はグレーアウト。
// #138: finding.target に一致する段は警告色＋(2) の明細へのリンクで強調する（SC-11 §(1)）。
function PipelineView({ stages, driftTargets }: { stages: PipelineStage[]; driftTargets: Set<string> }) {
  return (
    <Section title={`パイプライン段（${stages.length}）`}>
      {stages.length === 0 ? (
        <p>段は登録されていません。</p>
      ) : (
        <ol aria-label="パイプライン段" style={{ listStyle: 'none', padding: 0 }}>
          {stages.map((s) => {
            const drifted = driftTargets.has(s.name);
            return (
              <li
                key={s.name}
                style={{
                  border: '1px solid #ccc',
                  borderRadius: 6,
                  padding: '0.5rem 0.75rem',
                  margin: '0.4rem 0',
                  opacity: s.enabled ? 1 : 0.5,
                  ...driftStyle(drifted),
                }}
              >
                <div>
                  <strong>{s.name}</strong> <small>（{s.service}）</small>
                  {!s.enabled && <span> — 無効</span>}
                  {drifted && <DriftMark />}
                </div>
                <div>
                  <small>
                    consumer: {s.consumer}｜{s.input} → {s.outputs.length ? s.outputs.join(', ') : '（終端）'}
                  </small>
                </div>
              </li>
            );
          })}
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
