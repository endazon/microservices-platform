import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
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
import { i18n } from '@foundation/i18n';
import { PlatformRole, useHasAnyRole } from '@foundation/auth/roles';
import { toMessages } from '@foundation/utils/apiErrors';
import { DataSourceForm } from './DataSourceForm';
import { DataSourceAttributesForm } from './DataSourceAttributesForm';
import { formatDateTime, sourceTypeLabel, syncStateView } from '../types/syncState';
import { useDataSourceActions, useDataSources } from '../api/useDataSources';
// SC-06, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { DataSourceDto } from '@foundation/api/generated/bff.schemas';

// SC-06, UC-04, FR-01/FR-02: データソース管理画面（05_screens: ルート /admin/sources）。
// ソースの登録・一覧・同期状態の確認・手動同期を行い、SC-07（変換ジョブ）への導線を持つ。
//
// 未実装の要素（画面仕様書 docs/screens/SC-06_datasource-management.md §hi-fi モックアップとの対応）:
//   - **「⚠ 再試行中（3/5）」**: **［2026-08-08 / #537］実装した。** 契約が同期健全性
//     （`consecutiveFailureCount` / `retryLimit` / `lastSyncError`）を持ったため（裁定 Q14）。
//   - **「次回同期」列**: 契約（`nextSyncAt`）は #538 で揃ったが、**列の表示は未実装**である
//     （全ソース同値の共通間隔。IADR-0136）。
//   - **行操作「設定」**: **［2026-08-08 / #534］契約側の更新 API（PUT / PATCH）は揃った。**
//     **［2026-08-28 追記 / #754］既定属性（`confidentiality` / `department` / `lifecycle`）の
//     編集フォームを置いた**（計画 §SC-06「登録・**更新**フォームは既定属性 3 つを持つ」）。
//     接続先・認証情報の編集は依然として未実装であり、#534 の射程のまま残る。
//   もとの記録は projects/microservices-platform/10_feedback/20260805_sc05-07-admin-contract-gaps.md（planning#198 で裁定済み）。

export function DataSourceManagementPage() {
  const { t } = useLingui();
  const [formOpen, setFormOpen] = useState(false);
  // FR-05, UC-04, SC-06（#754）: 既定属性を編集中のソース ID（null なら閉じている）。
  // **行ごとにフォームを持たず、画面に 1 つだけ開く** —— 登録フォームと同じ扱いである。
  const [editingId, setEditingId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const sources = useDataSources();
  const actions = useDataSourceActions();
  const { create, patch, sync, disable } = actions;

  // FR-01, UC-04, SC-06（#628）: 計画 §SC-06「**登録・更新・無効化は管理者限定**」（裁定 Q19）。
  // **手動同期は含まない** —— planning#299（2026-08-09）が「実行系だが破壊的ではない」として
  // 運用者へ開いたままにすると裁定した。したがって出し分けるのは登録と無効化の 2 つだけである。
  // **実効境界はサーバ側**（`/bff/datasources` と後段の `AdminOnly`）であり、ここは表示制御にすぎない
  // （[[IADR-0039]] 決定 2）。閲覧ロール（ルートのゲート）は admin ＋ operator のまま据え置く。
  const canWrite = useHasAnyRole(PlatformRole.Admin);

  const items = sources.data ?? [];
  // 一覧から引く（ID だけを持つ）。一覧が再取得されれば編集フォームの初期値も最新に追随し、
  // 削除・無効化で行が消えたときはフォームも自然に閉じる。
  const editingSource = items.find((source) => source.id === editingId) ?? null;
  // IADR-0127 決定 7: 画面は**直近の操作の結果だけ**を出す。列挙は `useDataSourceActions()` の
  // 戻り値から導く——手書きの配列にすると、4 本目のミューテーションを足したときに同じ穴が空く。
  const mutations = Object.values(actions);
  const failed = mutations.find((m) => m.isError);

  /**
   * 新しい操作を始める前に、前回の結果（成功メッセージと各ミューテーションの失敗状態）を捨てる。
   *
   * TanStack Query は「**別の**ミューテーションが成功した」ことでは他方の `isError` を戻さない。
   * これが無いと「手動同期が失敗 → 無効化が成功」で成功バナーと古い失敗バナーが並び、
   * どの操作の結果なのかが読めなくなる（IADR-0127 決定 7）。
   */
  function beginOperation() {
    setNotice(null);
    for (const mutation of mutations) mutation.reset();
  }

  return (
    <section>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-lg font-semibold text-[--color-fg]">
          <Trans>データソース</Trans>
        </h1>
        {/* 押しても 403 になるボタンを置かない（#502 が確立した規則・[[IADR-0127]] 決定 1）。
            無言で消すと「登録できない画面」に見えるので、理由の文言を残す。 */}
        {canWrite ? (
          <Button
            type="button"
            variant="primary"
            onClick={() => {
              beginOperation();
              setFormOpen((open) => !open);
            }}
          >
            <Trans>＋ ソース登録</Trans>
          </Button>
        ) : (
          <span className="text-xs text-[--color-fg-muted]">
            <Trans>ソースの登録・無効化は管理者のみ実行できます</Trans>
          </span>
        )}
      </div>

      {formOpen && (
        <DataSourceForm
          submitting={create.isPending}
          onCancel={() => setFormOpen(false)}
          onSubmit={(input) => {
            beginOperation();
            create.mutate(
              { data: input },
              {
                onSuccess: () => {
                  setFormOpen(false);
                  setNotice(t`データソースを登録しました。`);
                },
              },
            );
          }}
        />
      )}

      {editingSource && (
        <DataSourceAttributesForm
          source={editingSource}
          submitting={patch.isPending}
          onCancel={() => setEditingId(null)}
          onSubmit={(input) => {
            beginOperation();
            patch.mutate(
              { id: editingSource.id, data: input },
              {
                onSuccess: () => {
                  setEditingId(null);
                  setNotice(t`既定属性を更新しました。`);
                },
              },
            );
          }}
        />
      )}

      {notice && (
        <Alert tone="success" role="status" className="mb-2" label={t`完了`}>
          {notice}
        </Alert>
      )}
      {failed && (
        <Alert tone="danger" role="alert" className="mb-2" label={t`エラー`}>
          {toMessages(failed.error, t`操作を実行できませんでした。`).join(' / ')}
        </Alert>
      )}

      {sources.isPending && (
        <p role="status" className="text-sm text-[--color-fg-muted]">
          <Trans>読み込み中…</Trans>
        </p>
      )}

      {/* BFF は後段障害を空一覧へ縮退させない（502 で可視化する）。「未登録」と誤認させて
          重複登録を招かないためであり、画面も取得失敗を 0 件表示へ寄せない。 */}
      {sources.isError && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          {toMessages(sources.error, t`データソースを取得できませんでした。`).join(' / ')}
        </Alert>
      )}

      {sources.isSuccess &&
        (items.length === 0 ? (
          <p className="text-sm">
            <Trans>データソースは登録されていません。</Trans>
          </p>
        ) : (
          <Table>
            <TableCaption>
              <Trans>登録済みデータソースの一覧</Trans>
            </TableCaption>
            <TableHead>
              <TableRow>
                <TableHeaderCell>
                  <Trans>ソース</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>種別</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>同期状態</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>操作</Trans>
                </TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {items.map((source) => (
                <SourceRow
                  key={source.id}
                  source={source}
                  canWrite={canWrite}
                  busy={sync.isPending || disable.isPending}
                  onEditAttributes={() => {
                    beginOperation();
                    setEditingId(source.id);
                  }}
                  onSync={() => {
                    beginOperation();
                    sync.mutate(
                      { id: source.id },
                      {
                        onSuccess: () => setNotice(t`同期をトリガしました。`),
                      },
                    );
                  }}
                  onDisable={() => {
                    beginOperation();
                    disable.mutate(
                      { id: source.id },
                      {
                        onSuccess: () => setNotice(t`データソースを無効化しました。`),
                      },
                    );
                  }}
                />
              ))}
            </TableBody>
          </Table>
        ))}

      {/* 計画の遷移図 SC06 → SC07（取り込み → 変換の運用フロー）。 */}
      <p className="mt-4 text-sm">
        <Link to="/admin/conversions" className="text-[--color-brand] hover:underline">
          <Trans>変換ジョブの状況を見る →</Trans>
        </Link>
      </p>

      {/* 05_screens §SC-06 主要素の注記。静的な注記なので role は付けない。 */}
      <Alert tone="info" className="mt-3" label={t`注記`}>
        <Trans>
          接続情報（認証情報）は Vault 管理です。接続の継続失敗はアラートで通知されます。
        </Trans>
      </Alert>
    </section>
  );
}

function SourceRow({
  source,
  canWrite,
  busy,
  onEditAttributes,
  onSync,
  onDisable,
}: {
  source: DataSourceDto;
  canWrite: boolean;
  busy: boolean;
  onEditAttributes: () => void;
  onSync: () => void;
  onDisable: () => void;
}) {
  // INDEX 決定 21: 同期状態は色だけで意味を持たせない。StatusBadge が tone ごとの固定アイコンと
  // テキストを型で強制する。警告色（琥珀）は計画が**同期異常**へ与えた色であり、
  // **#537 で契約が同期健全性を持ったので充て先が確定した**（無効は中立のまま。IADR-0127 決定 2）。
  const state = syncStateView(source.status, source.lastSyncedAt, {
    consecutiveFailureCount: source.consecutiveFailureCount,
    retryLimit: source.retryLimit,
  });
  const typeLabel = sourceTypeLabel(source.sourceType);

  return (
    <TableRow>
      <TableCell>
        <span className="font-medium">{source.name}</span>
        <p className="text-xs text-[--color-fg-muted]">
          <code>{source.connectionUri}</code>
        </p>
      </TableCell>
      <TableCell>
        <Tag tone="neutral">{typeof typeLabel === 'string' ? typeLabel : i18n._(typeLabel)}</Tag>
      </TableCell>
      <TableCell>
        <StatusBadge tone={state.tone}>{i18n._(state.label)}</StatusBadge>
        {state.showSyncedAt && (
          <p className="text-xs text-[--color-fg-muted]">{formatDateTime(source.lastSyncedAt)}</p>
        )}
        {/* SC-06（Q14 / #537）: 直近エラーは「なぜ止まったか」の唯一の手掛かりである。値は
            サービス側でマスク済み（IADR-0053 と同じ守り）。異常時だけ出す。 */}
        {state.tone === 'warning' && source.lastSyncError && (
          <p
            className="text-xs text-[--color-fg-muted]"
            title={formatDateTime(source.lastSyncErrorAt)}
          >
            <code>{source.lastSyncError}</code>
          </p>
        )}
      </TableCell>
      <TableCell>
        <span className="flex flex-wrap gap-2">
          {/* 手動同期は運用者にも開いたままである（planning#299・2026-08-09 の裁定）。
              運用者が異常に気づいたその場で一次対応できることを優先する。 */}
          <Button type="button" size="sm" disabled={busy} onClick={onSync}>
            <Trans>手動同期</Trans>
          </Button>
          {/* FR-05, UC-04, SC-06（#754）: 既定属性の編集は**更新**にあたるため管理者限定である
              （計画 §SC-06「登録・更新・無効化は管理者限定」）。**無効なソースでも開く** ——
              無効化は論理削除であり、再有効化に備えた属性の整備は無効中にも起こる
              （バックエンドの更新系が `disabled` を弾かないのと同じ理由）。 */}
          {canWrite && (
            <Button type="button" size="sm" disabled={busy} onClick={onEditAttributes}>
              <Trans>既定属性</Trans>
            </Button>
          )}
          {/* 無効化は管理者限定（#628）。既に無効なソースへ再度無効化を送らない。
           **理由の文言は画面の先頭に 1 つだけ置く**——行ごとに繰り返すと一覧が読めなくなる。 */}
          {canWrite && source.status !== 'disabled' && (
            <Button type="button" size="sm" disabled={busy} onClick={onDisable}>
              <Trans>無効化</Trans>
            </Button>
          )}
        </span>
      </TableCell>
    </TableRow>
  );
}
