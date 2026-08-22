import type { ReactNode } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import type { MessageDescriptor } from '@lingui/core';
import {
  Alert,
  Button,
  StatusBadge,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { i18n } from '@foundation/i18n';
import { toMessages } from '@foundation/ui/apiErrors';
// ADR-0031 §採用技術一覧（日付 = dayjs）/ #788: 同名のローカル実装を持っていたが、
// **同じ整形規則を 2 か所に置かない**ため foundation の 1 本へ寄せた。
import { formatDateTime } from '@foundation/ui/formatDateTime';
import { driftKindLabel, driftSeverityView, driftTargets, hadDriftLabel } from '../types/driftView';
import {
  useConfigDrift,
  useConfigHistory,
  useEffectiveConfig,
  useRefreshConfigViewer,
} from '../api/useConfigViewer';
// SC-11, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO**である。
// 手書きの写しを置かない——契約が変われば型検査が落ちる状態にしておく。
import type {
  ConfigVersionDto,
  ConfigVersionEntryDto,
  ConnectorDto,
  DriftReportDto,
  EventBindingDto,
  PipelineStageDto,
  PortSelectionDto,
} from '@foundation/api/generated/bff.schemas';

// SC-11, FR-15, ADR-0018: 構成ビューア（05_screens: ルート /admin/config-viewer）。
// 実効構成・宣言（Git）との差分・構成バージョン履歴を参照専用で可視化する。
//
// 可視化はグラフ描画ライブラリを使わず CSS 縦チェーン＋表で表現する（IADR-0036）。折りたたみは
// ネイティブの <details>/<summary> —— 計画 13_frontend-stack §shadcn/ui 派生の範囲 の 4 基準の
// いずれにも該当しないため @platform/ui へは入れない（IADR-0129 決定 7）。
//
// IADR-0129 決定 5: 3 本の問い合わせは独立に扱い、領域ごとに縮退する。ただし**実効構成が
// 取れなければ他の 2 領域も出さない**（何に対する差分か読めないため）。
//
// 履歴の件数は画面で切り詰めない —— 保持範囲は GitOps 側が決める（IADR-0046・計画 2026-08-04 確定）。

/** ドリフト明細セクションの id（チェーンの警告からページ内リンクで飛ぶ先）。 */
const DRIFT_SECTION_ID = 'sc11-drift-section';

/** MessageDescriptor を描画時に解決する（モジュール初期化時に確定させるとロケール切替に追随しない）。 */
function labelOf(label: MessageDescriptor | string): string {
  return typeof label === 'string' ? label : i18n._(label);
}

/** コミット ID は先頭 7 桁で示す（完全な値は title 属性に残す）。 */
function shortCommit(commit: string | null | undefined): string {
  return commit ? commit.slice(0, 7) : '—';
}

/** 404 か（＝存在秘匿として中立文言へ寄せる対象か）。それ以外の失敗は系の状態であり秘匿しない。 */
function isHidden(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'notFound';
}

export function ConfigViewerPage() {
  const { t } = useLingui();
  const config = useEffectiveConfig();
  const drift = useConfigDrift();
  const history = useConfigHistory();
  const refresh = useRefreshConfigViewer();

  // IADR-0009: 404 は「不在」と「権限による秘匿」を区別しない。中立の文言へ寄せる。
  // IADR-0129 決定 3 は**従の問い合わせにも同じく効く**——「区別しないと運用者が『権限が無い』と
  // 誤読して障害を見逃す」という理由は、ドリフト・履歴が落ちた場合にも等しく当てはまる。
  const configHidden = isHidden(config.error);
  const driftHidden = isHidden(drift.error);
  const historyHidden = isHidden(history.error);

  return (
    <section>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <h1 className="mr-auto text-lg font-semibold text-[--color-fg]">
          <Trans>実効構成</Trans>
        </h1>
        {/* ヘッダの表示は**実効構成が取れたときだけ**である。ドリフトのバッジも例外ではない
            ——バッジだけがヘッダに残ると「何に対する差分か読めないドリフト件数」になる
            （IADR-0129 決定 5）。3 本のクエリは独立に走るため、実効構成が 5xx でドリフトが 200
            という組み合わせが実際に起こる。 */}
        {config.isSuccess && config.data && (
          <>
            <ConfigVersionTags version={config.data.version} />
            {/* ドリフトのバッジは**取得できたときだけ**出す（出せないときの「0 件」と紛れるため）。 */}
            {drift.isSuccess && drift.data && <DriftBadge report={drift.data} />}
          </>
        )}
        <Button type="button" size="sm" onClick={refresh}>
          <Trans>再取得</Trans>
        </Button>
      </div>

      {config.isPending && (
        <p role="status" className="text-sm text-[--color-fg-muted]">
          <Trans>読み込み中…</Trans>
        </p>
      )}

      {config.isError && configHidden && (
        <p className="text-sm">
          <Trans>構成情報は利用できません。</Trans>
        </p>
      )}
      {config.isError && !configHidden && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          {toMessages(config.error, t`構成情報を取得できませんでした。`).join(' / ')}
        </Alert>
      )}

      {config.isSuccess && config.data && (
        <>
          <Fold summary={t`(1) 実効構成 — パイプライン段・接続`} defaultOpen>
            <PipelineChain
              stages={config.data.pipeline}
              drifted={driftTargets(drift.data?.findings ?? [])}
            />
            <EventBindingsTable bindings={config.data.eventBindings} />
            <PortsTable ports={config.data.ports} />
            <ConnectorsTable connectors={config.data.connectors} />
          </Fold>

          <Fold summary={t`(2) 宣言（Git）との差分 — ドリフト`} defaultOpen id={DRIFT_SECTION_ID}>
            {drift.isPending && (
              <p role="status" className="text-sm text-[--color-fg-muted]">
                <Trans>ドリフトを確認中…</Trans>
              </p>
            )}
            {drift.isError && driftHidden && (
              <p className="text-sm">
                <Trans>ドリフト情報は利用できません。</Trans>
              </p>
            )}
            {drift.isError && !driftHidden && (
              <Alert tone="danger" role="alert" label={t`エラー`}>
                {toMessages(drift.error, t`ドリフト情報を取得できませんでした。`).join(' / ')}
              </Alert>
            )}
            {drift.isSuccess && drift.data && <DriftTable report={drift.data} />}
          </Fold>

          <Fold summary={t`(3) 構成バージョン履歴（新しい順）`}>
            {history.isPending && (
              <p role="status" className="text-sm text-[--color-fg-muted]">
                <Trans>履歴を確認中…</Trans>
              </p>
            )}
            {history.isError && historyHidden && (
              <p className="text-sm">
                <Trans>バージョン履歴は利用できません。</Trans>
              </p>
            )}
            {history.isError && !historyHidden && (
              <Alert tone="danger" role="alert" label={t`エラー`}>
                {toMessages(history.error, t`バージョン履歴を取得できませんでした。`).join(' / ')}
              </Alert>
            )}
            {history.isSuccess && <HistoryTable entries={history.data} />}
          </Fold>

          <Alert tone="info" className="mt-3" label={t`参照のみ`}>
            <Trans>
              構成の変更は Git
              経由（GitOps）に限ります。本画面から構成は変更できません。閲覧は監査ログに記録されます。
            </Trans>
          </Alert>
        </>
      )}
    </section>
  );
}

/** ヘッダの構成バージョン（コミット・適用日時・適用者）。分類の名前なので Tag を使う。 */
function ConfigVersionTags({ version }: { version: ConfigVersionDto }) {
  const { t } = useLingui();
  // lingui/no-expression-in-message: 補間には**素の変数だけ**を置く（式を入れると抽出された
  // メッセージから元の値が読めなくなり、翻訳者が文脈を判断できない）。
  const commit = shortCommit(version.gitCommit);
  const appliedAt = formatDateTime(version.appliedAt);
  const appliedBy = version.appliedBy ?? '—';
  return (
    <>
      <Tag title={version.gitCommit ?? undefined}>{t`コミット ${commit}`}</Tag>
      <Tag>{t`適用 ${appliedAt}`}</Tag>
      <Tag>{t`適用者 ${appliedBy}`}</Tag>
    </>
  );
}

/** 全体ドリフト状態。INDEX 決定 21: 色だけで意味を持たせない（StatusBadge が型で強制する）。 */
function DriftBadge({ report }: { report: DriftReportDto }) {
  const { t } = useLingui();
  const count = report.findings.length;
  return count === 0 ? (
    <StatusBadge tone="success">{t`ドリフトなし`}</StatusBadge>
  ) : (
    <StatusBadge tone="warning">{t`ドリフト ${count} 件`}</StatusBadge>
  );
}

/** 折りたたみセクション（ネイティブ <details>）。 */
function Fold({
  summary,
  children,
  defaultOpen = false,
  id,
}: {
  summary: string;
  children: ReactNode;
  defaultOpen?: boolean;
  id?: string;
}) {
  return (
    <details
      id={id}
      open={defaultOpen}
      className="mb-3 rounded-[--radius-control] border border-[--color-border] bg-[--color-surface]"
    >
      <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-[--color-fg]">
        {summary}
      </summary>
      <div className="flex flex-col gap-3 px-3 pb-3">{children}</div>
    </details>
  );
}

/** パイプライン段の CSS 縦チェーン（IADR-0036）。無効段は淡色 ＋ バッジ、ドリフト段は警告色 ＋ 明細へのリンク。 */
function PipelineChain({ stages, drifted }: { stages: PipelineStageDto[]; drifted: Set<string> }) {
  const { t } = useLingui();
  if (stages.length === 0) {
    return (
      <p className="text-sm">
        <Trans>段は登録されていません。</Trans>
      </p>
    );
  }
  return (
    <ol aria-label={t`パイプライン段`} className="flex flex-col gap-1">
      {stages.map((stage) => {
        const isDrifted = drifted.has(stage.name);
        return (
          <li
            key={stage.name}
            className={[
              'rounded-[--radius-control] border px-3 py-2 text-sm',
              isDrifted ? 'border-[--color-warning]' : 'border-[--color-border]',
              stage.enabled ? '' : 'opacity-60',
            ].join(' ')}
          >
            <div className="flex flex-wrap items-center gap-2">
              <strong className="text-[--color-fg]">{stage.name}</strong>
              <span className="text-xs text-[--color-fg-muted]">（{stage.service}）</span>
              {/* 無効は淡色だけで示さない（INDEX 決定 21）。 */}
              {!stage.enabled && <StatusBadge tone="neutral">{t`無効`}</StatusBadge>}
              {isDrifted && (
                <a
                  href={`#${DRIFT_SECTION_ID}`}
                  className="text-xs text-[--color-warning] hover:underline"
                >
                  <Trans>⚠ ドリフト（明細へ）</Trans>
                </a>
              )}
            </div>
            <div className="text-xs text-[--color-fg-muted]">
              {t`consumer`}: {stage.consumer}｜{stage.input} →{' '}
              {stage.outputs.length > 0 ? stage.outputs.join(', ') : t`（終端）`}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

function EventBindingsTable({ bindings }: { bindings: EventBindingDto[] }) {
  const { t } = useLingui();
  return (
    <div>
      <h2 className="mb-1 text-sm font-medium text-[--color-fg-muted]">
        <Trans>イベント接続</Trans>
      </h2>
      {bindings.length === 0 ? (
        <p className="text-sm">
          <Trans>イベント接続はありません。</Trans>
        </p>
      ) : (
        <Table>
          <TableCaption>{t`イベント接続の一覧`}</TableCaption>
          <TableHead>
            <TableRow>
              <TableHeaderCell>
                <Trans>イベント</Trans>
              </TableHeaderCell>
              <TableHeaderCell>
                <Trans>発行者</Trans>
              </TableHeaderCell>
              <TableHeaderCell>
                <Trans>購読者</Trans>
              </TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {bindings.map((b) => (
              <TableRow key={b.event}>
                <TableCell>{b.event}</TableCell>
                <TableCell>{b.publishers.join(', ') || '—'}</TableCell>
                <TableCell>{b.subscribers.join(', ') || '—'}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function PortsTable({ ports }: { ports: PortSelectionDto[] }) {
  const { t } = useLingui();
  return (
    <div>
      <h2 className="mb-1 text-sm font-medium text-[--color-fg-muted]">
        <Trans>ポート実装の選択</Trans>
      </h2>
      {ports.length === 0 ? (
        <p className="text-sm">
          <Trans>ポートはありません。</Trans>
        </p>
      ) : (
        <Table>
          <TableCaption>{t`ポート実装の選択の一覧`}</TableCaption>
          <TableHead>
            <TableRow>
              <TableHeaderCell>
                <Trans>ポート</Trans>
              </TableHeaderCell>
              <TableHeaderCell>
                <Trans>実装</Trans>
              </TableHeaderCell>
              <TableHeaderCell>
                <Trans>接続先</Trans>
              </TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {ports.map((p) => (
              <TableRow key={p.port}>
                <TableCell>{p.port}</TableCell>
                <TableCell>{p.implementation}</TableCell>
                <TableCell>{p.target ?? '—'}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function ConnectorsTable({ connectors }: { connectors: ConnectorDto[] }) {
  const { t } = useLingui();
  return (
    <div>
      <h2 className="mb-1 text-sm font-medium text-[--color-fg-muted]">
        <Trans>コネクタ</Trans>
      </h2>
      {connectors.length === 0 ? (
        <p className="text-sm">
          <Trans>コネクタはありません。</Trans>
        </p>
      ) : (
        <Table>
          <TableCaption>{t`コネクタの一覧`}</TableCaption>
          <TableHead>
            <TableRow>
              <TableHeaderCell>
                <Trans>コネクタ</Trans>
              </TableHeaderCell>
              <TableHeaderCell>
                <Trans>状態</Trans>
              </TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {connectors.map((c) => (
              <TableRow key={c.name}>
                <TableCell>{c.name}</TableCell>
                <TableCell>
                  <StatusBadge tone={c.enabled ? 'success' : 'neutral'}>
                    {c.enabled ? t`有効` : t`無効`}
                  </StatusBadge>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

/** ドリフト明細。0 件のときも**確認時刻**を出す（未検出と未実行を区別できるようにする）。 */
function DriftTable({ report }: { report: DriftReportDto }) {
  const { t } = useLingui();
  if (report.findings.length === 0) {
    return (
      <p className="text-sm">
        <Trans>ドリフトなし（OK）。</Trans>{' '}
        <span className="text-xs text-[--color-fg-muted]">
          {t`確認時刻`}: {formatDateTime(report.checkedAt)}
        </span>
      </p>
    );
  }
  return (
    <>
      <p className="text-xs text-[--color-fg-muted]">
        {t`確認時刻`}: {formatDateTime(report.checkedAt)}
      </p>
      <Table>
        <TableCaption>{t`ドリフト明細の一覧`}</TableCaption>
        <TableHead>
          <TableRow>
            <TableHeaderCell>
              <Trans>種別</Trans>
            </TableHeaderCell>
            <TableHeaderCell>
              <Trans>深刻度</Trans>
            </TableHeaderCell>
            <TableHeaderCell>
              <Trans>対象</Trans>
            </TableHeaderCell>
            <TableHeaderCell>
              <Trans>説明</Trans>
            </TableHeaderCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {report.findings.map((f, i) => {
            const severity = driftSeverityView(f.severity);
            return (
              <TableRow key={`${f.kind}-${f.target}-${i}`}>
                <TableCell>{labelOf(driftKindLabel(f.kind))}</TableCell>
                <TableCell>
                  {/* INDEX 決定 21: 深刻度は色 ＋ アイコン ＋ テキストで示す。 */}
                  <StatusBadge tone={severity.tone}>{labelOf(severity.label)}</StatusBadge>
                </TableCell>
                <TableCell>{f.target}</TableCell>
                <TableCell>{f.detail}</TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </>
  );
}

/** 構成バージョン履歴（新しい順）。並び順とデータ源は API が担い、画面は表示のみ。 */
function HistoryTable({ entries }: { entries: ConfigVersionEntryDto[] }) {
  const { t } = useLingui();
  if (entries.length === 0) {
    return (
      <p className="text-sm">
        <Trans>適用履歴はありません。</Trans>
      </p>
    );
  }
  return (
    <Table>
      <TableCaption>{t`構成バージョン履歴の一覧`}</TableCaption>
      <TableHead>
        <TableRow>
          <TableHeaderCell>
            <Trans>コミット</Trans>
          </TableHeaderCell>
          <TableHeaderCell>
            <Trans>適用日時</Trans>
          </TableHeaderCell>
          <TableHeaderCell>
            <Trans>適用者</Trans>
          </TableHeaderCell>
          <TableHeaderCell>
            <Trans>ドリフト</Trans>
          </TableHeaderCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {entries.map((h, i) => (
          // 注入元が同一 commit + appliedAt の重複を返しても衝突しないよう index を前置する。
          <TableRow key={`${i}-${h.gitCommit ?? 'nocommit'}-${h.appliedAt ?? ''}`}>
            <TableCell>
              <code title={h.gitCommit ?? undefined}>{shortCommit(h.gitCommit)}</code>
            </TableCell>
            <TableCell>{formatDateTime(h.appliedAt)}</TableCell>
            <TableCell>{h.appliedBy ?? '—'}</TableCell>
            <TableCell>{labelOf(hadDriftLabel(h.hadDrift))}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
