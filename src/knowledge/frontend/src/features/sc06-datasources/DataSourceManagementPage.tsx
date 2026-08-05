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
import { toMessages } from '@foundation/ui/apiErrors';
import { DataSourceForm } from './DataSourceForm';
import { formatDateTime, sourceTypeLabel, syncStateView } from './syncState';
import { useDataSourceActions, useDataSources } from './useDataSources';
import type { DataSource } from './useDataSources';

// SC-06, UC-04, FR-01/FR-02: データソース管理画面（05_screens: ルート /admin/sources）。
// ソースの登録・一覧・同期状態の確認・手動同期を行い、SC-07（変換ジョブ）への導線を持つ。
//
// 実装しない要素（画面仕様書 docs/screens/SC-06_datasource-management.md §hi-fi モックアップとの対応）:
//   - **「⚠ 再試行中（3/5）」**: `DataSourceDto.Status` は active / disabled の 2 値のみで、
//     連続失敗回数・再試行上限を持つフィールドが無い。
//   - **「次回同期」列**: 同期は全ソース共通の間隔で回る hosted service であり、
//     ソース別スケジュールという概念が契約に無い（常に空の列になる）。
//   - **行操作「設定」**: `/bff/datasources` に更新（PUT / PATCH）が無い（押しても何も変わらない）。
//   いずれも feedback/20260805_sc05-07-admin-contract-gaps.md に環流の記録を作成した。

export function DataSourceManagementPage() {
  const { t } = useLingui();
  const [formOpen, setFormOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const sources = useDataSources();
  const actions = useDataSourceActions();
  const { create, sync, disable } = actions;

  const items = sources.data ?? [];
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
      </div>

      {formOpen && (
        <DataSourceForm
          submitting={create.isPending}
          onCancel={() => setFormOpen(false)}
          onSubmit={(input) => {
            beginOperation();
            create.mutate(input, {
              onSuccess: () => {
                setFormOpen(false);
                setNotice(t`データソースを登録しました。`);
              },
            });
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
                  busy={sync.isPending || disable.isPending}
                  onSync={() => {
                    beginOperation();
                    sync.mutate(source.id, {
                      onSuccess: () => setNotice(t`同期をトリガしました。`),
                    });
                  }}
                  onDisable={() => {
                    beginOperation();
                    disable.mutate(source.id, {
                      onSuccess: () => setNotice(t`データソースを無効化しました。`),
                    });
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
  busy,
  onSync,
  onDisable,
}: {
  source: DataSource;
  busy: boolean;
  onSync: () => void;
  onDisable: () => void;
}) {
  // INDEX 決定 21: 同期状態は色だけで意味を持たせない。StatusBadge が tone ごとの固定アイコンと
  // テキストを型で強制する。警告色（琥珀）は計画が**同期異常**へ与えた色であり、契約が同期健全性を
  // 持つまで**どの状態にも充てない**（無効は中立。05_screens モック間相違の確定 ②。IADR-0127 決定 2）。
  const state = syncStateView(source.status, source.lastSyncedAt);
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
      </TableCell>
      <TableCell>
        <span className="flex flex-wrap gap-2">
          <Button type="button" size="sm" disabled={busy} onClick={onSync}>
            <Trans>手動同期</Trans>
          </Button>
          {/* 既に無効なソースへ再度無効化を送らない。 */}
          {source.status !== 'disabled' && (
            <Button type="button" size="sm" disabled={busy} onClick={onDisable}>
              <Trans>無効化</Trans>
            </Button>
          )}
        </span>
      </TableCell>
    </TableRow>
  );
}
