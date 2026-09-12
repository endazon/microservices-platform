import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  EmptyState,
  Panel,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import { toMessages } from '@foundation/utils/apiErrors';
import type {
  SyncConflictDetailDto,
  SyncConflictSummaryDto,
} from '@foundation/api/generated/bff.schemas';
import { ConfirmDialog } from '../../../components/ConfirmDialog';
import {
  useSyncConflictActions,
  useSyncConflictDetail,
  useSyncConflicts,
} from '../api/useSyncConflicts';

// SC-20 主要素 5, UC-11, FR-20, ADR-0037 決定 7 / IADR-0352: 競合の一覧・2 ペイン差分・3 択。
//
// ■ 🔴 **自動解決の選択肢を置かない。** 契約にも「後勝ち」に相当する値が無い（決定 7）。
//   利用者が**両方の本文を見たうえで**選ぶ、というのが計画の要求である。
//   「置いていない」ことは単体テストが**陽性対照（3 択が在る）と対で**固定する。
//
// ■ 🔴 **本文は選んだ 1 件だけを引く。** 一覧の応答は本文を持たない（契約の注記）。
//   未選択のときは詳細の問い合わせ自体を描かない（生成フックの `enabled` は
//   id が空文字でも真になるため、**部品のマウントで制御する**）。
//
// ■ 3 択の結果は**取り返しがつかない**ものが 1 つある（`server` は端末側の本文を捨てる）。
//   確認ダイアログの本文で**何が失われるか**を択ごとに言い分ける —— 「本当によろしいですか？」
//   だけの確認は、3 択のどれを選んでも同じ文面になり、確認として働かない。

/** 契約 `ResolveSyncConflictRequest.resolution` の 3 値。 */
type Resolution = 'local' | 'server' | 'both';

export function SyncConflictsPanel() {
  const { t } = useLingui();
  const conflicts = useSyncConflicts();
  const { resolve } = useSyncConflictActions();

  /** 詳細を開いている競合。開いていないときは `null`。 */
  const [selectedId, setSelectedId] = useState<string | null>(null);
  /** 確認ダイアログで待っている択。開いていないときは `null`。 */
  const [confirming, setConfirming] = useState<Resolution | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const rows = conflicts.data ?? [];
  const selected = rows.find((c) => c.id === selectedId);
  const pending = resolve.isPending;

  function beginOperation() {
    setNotice(null);
    resolve.reset();
  }

  function confirmResolve() {
    if (confirming === null || selectedId === null) return;
    const resolution = confirming;
    beginOperation();
    resolve.mutate(
      { id: selectedId, data: { resolution } },
      {
        onSuccess: () => {
          setSelectedId(null);
          setNotice(
            resolution === 'local'
              ? t`ローカルの本文を新しい版として保存しました。`
              : resolution === 'server'
                ? t`サーバの本文を採用しました。`
                : t`両方を残しました。別名の資料を作成しています。`,
          );
        },
      },
    );
    setConfirming(null);
  }

  // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す。
  const selectedTitle = selected?.title ?? '';

  return (
    <Panel aria-label={t`同期の競合`} heading={<Trans>同期の競合</Trans>} headingAs="h2">
      <div className="flex flex-col gap-2">
        <p className="text-sm">
          <Trans>
            同じ資料が端末とサーバの両方で編集されたときに競合として記録されます。どちらを採るかは、両方の本文を見てあなたが選びます。
          </Trans>
        </p>

        {notice && (
          <Alert tone="success" label={t`完了`} role="status">
            {notice}
          </Alert>
        )}
        {resolve.isError && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {toMessages(
              resolve.error,
              t`競合を解決できませんでした。時間をおいて再度お試しください。`,
            ).join(' ')}
          </Alert>
        )}

        <QueryState
          query={conflicts}
          isEmpty={(list: SyncConflictSummaryDto[]) => list.length === 0}
          loadingLabel={t`同期の競合を読み込み中…`}
          errorTitle={t`同期の競合を取得できませんでした。`}
          empty={
            <EmptyState
              title={t`競合はありません。`}
              description={t`端末とサーバで同じ資料が別々に編集されると、ここに現れます。`}
            />
          }
        >
          {() => (
            <Table>
              <TableCaption>{t`未解決の同期競合`}</TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell scope="col">
                    <Trans>タイトル</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>パス</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>検出日時</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>端末</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>版</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>操作</Trans>
                  </TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map((conflict) => (
                  <ConflictRow
                    key={conflict.id}
                    conflict={conflict}
                    opened={conflict.id === selectedId}
                    disabled={pending}
                    onOpen={() => {
                      beginOperation();
                      setSelectedId(conflict.id);
                    }}
                  />
                ))}
              </TableBody>
            </Table>
          )}
        </QueryState>

        {selectedId !== null && (
          <ConflictDetail
            conflictId={selectedId}
            disabled={pending}
            onChoose={(resolution) => setConfirming(resolution)}
          />
        )}
      </div>

      {confirming !== null && (
        <ConfirmDialog
          title={
            confirming === 'local'
              ? t`ローカルの本文を採用しますか？`
              : confirming === 'server'
                ? t`サーバの本文を採用しますか？`
                : t`両方を残しますか？`
          }
          confirmLabel={
            confirming === 'local'
              ? t`ローカルを採用する`
              : confirming === 'server'
                ? t`サーバを採用する`
                : t`両方を残す`
          }
          cancelLabel={t`やめる`}
          // 🔴 端末側の本文を捨てる択だけを破壊的として扱う（3 択を一律に赤くしない）。
          destructive={confirming === 'server'}
          pending={pending}
          onConfirm={confirmResolve}
          onCancel={() => setConfirming(null)}
        >
          <p>
            <Trans>対象: 「{selectedTitle}」</Trans>
          </p>
          <p>
            {confirming === 'local' ? (
              <Trans>
                端末の本文が新しい版になります。サーバ上の現在の本文は、1
                つ前の版として版履歴に残ります。
              </Trans>
            ) : confirming === 'server' ? (
              <Trans>
                サーバの本文をそのまま採ります。端末側の本文は失われます。この操作は元に戻せません。
              </Trans>
            ) : (
              <Trans>
                サーバの本文はそのまま残し、端末の本文を別名の資料として新しく作ります。どちらの本文も失われません。
              </Trans>
            )}
          </p>
        </ConfirmDialog>
      )}
    </Panel>
  );
}

/** 一覧の 1 行。開いている行は「開いている」ことを**文言で**示す（色だけに頼らない）。 */
function ConflictRow({
  conflict,
  opened,
  disabled,
  onOpen,
}: {
  conflict: SyncConflictSummaryDto;
  opened: boolean;
  disabled: boolean;
  onOpen: () => void;
}) {
  const { t } = useLingui();
  // ［2026-08-30 / #1078］翻訳文へは単純な変数で渡す。
  const localVersion = conflict.localBaseVersion;
  const serverVersion = conflict.serverVersion;

  return (
    <TableRow>
      <TableCell>{conflict.title}</TableCell>
      <TableCell>{conflict.vaultPath}</TableCell>
      <TableCell>{formatDateTime(conflict.detectedAt)}</TableCell>
      <TableCell>{conflict.deviceName}</TableCell>
      <TableCell>{t`ローカル ${localVersion} 版 ／ サーバ ${serverVersion} 版`}</TableCell>
      <TableCell>
        <Button size="sm" variant="secondary" disabled={disabled || opened} onClick={onOpen}>
          {opened ? t`表示中` : t`本文を見て解決する`}
        </Button>
      </TableCell>
    </TableRow>
  );
}

/**
 * 2 ペイン差分と 3 択。**選んでいるときだけマウントされる**（上の注記）。
 *
 * 差分は**行単位の色分けをしない**。色分けは「どちらが正しいか」を暗示しうるが、
 * 決めるのは利用者である（ADR-0037 決定 7）。両版を並べて読める形で足りる。
 */
function ConflictDetail({
  conflictId,
  disabled,
  onChoose,
}: {
  conflictId: string;
  disabled: boolean;
  onChoose: (resolution: Resolution) => void;
}) {
  const { t } = useLingui();
  const detail = useSyncConflictDetail(conflictId);

  return (
    <QueryState
      query={detail}
      loadingLabel={t`競合の本文を読み込み中…`}
      errorTitle={t`競合の本文を取得できませんでした。`}
    >
      {(data: SyncConflictDetailDto) => (
        <section aria-label={t`競合の解決`} className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">{data.title}</h3>
          <div className="grid gap-2 md:grid-cols-2">
            <div className="flex flex-col gap-1">
              <h4 className="text-xs font-medium text-fg-muted">
                <Trans>ローカル版（端末の本文）</Trans>
              </h4>
              <pre
                aria-label={t`ローカル版の本文`}
                className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded-sm border border-divider bg-bg p-2 text-xs"
              >
                {data.localContent}
              </pre>
            </div>
            <div className="flex flex-col gap-1">
              <h4 className="text-xs font-medium text-fg-muted">
                <Trans>サーバ版（いま保存されている本文）</Trans>
              </h4>
              <pre
                aria-label={t`サーバ版の本文`}
                className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded-sm border border-divider bg-bg p-2 text-xs"
              >
                {data.serverContent}
              </pre>
            </div>
          </div>
          {/* 🔴 3 択だけを置く。自動解決の択は無い（上の注記）。 */}
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" disabled={disabled} onClick={() => onChoose('local')}>
              <Trans>ローカルを採用</Trans>
            </Button>
            <Button variant="secondary" disabled={disabled} onClick={() => onChoose('server')}>
              <Trans>サーバを採用</Trans>
            </Button>
            <Button variant="secondary" disabled={disabled} onClick={() => onChoose('both')}>
              <Trans>両方を残す（別名保存）</Trans>
            </Button>
          </div>
        </section>
      )}
    </QueryState>
  );
}
