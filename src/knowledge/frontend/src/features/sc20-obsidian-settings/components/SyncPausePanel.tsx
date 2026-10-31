import { Trans, useLingui } from '@lingui/react/macro';
import { Alert, Button, Note, Panel, StatusBadge } from '@platform/ui';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import { toMessages } from '@foundation/utils/apiErrors';
import { useSyncPauseActions, useSyncSettings } from '../api/useSyncSettings';

// FR-20, UC-11, SC-20 主要素 7, 計画 ADR-0145 決定 1・5, [[IADR-0668]] (#2379): **同期の状態と一時停止・再開**。
//
// ■ **利用者単位の操作を 1 つだけ置く**（全端末）。端末一覧の行ごとには置かない（決定 5）。端末ごとに止めたいなら個別失効がある。
// ■ 🔴 **「一時停止中」は ADR-0145 の一時停止だけを指す。** トークンの期限切れ（端末の行の「期限切れ（同期は停止しています）」）や
//   同期停止（対象フォルダから外すこと。ADR-0037 決定 4）を、この区画の状態に混ぜない。状態の正は同期設定の `syncPaused` だけである。
// ■ 操作の近くで計画の 2 点（すべての端末が止まる・トークンは残り再開で戻る）を常に伝える。期限は止まらないことも添える
//   （停止したまま期限が切れると、再開しても再発行が要る。ADR-0145 §結果）。
// ■ **確認ダイアログは置かない。** 再開で戻せる操作で、データも消さない（戻せない失効とは重さが違う）。
// ■ 止めるのはサーバである（子 A・IADR-0667）。サーバの拒否が配備された後でだけ、この操作を置く（ADR-0145 フォローアップ 2）。
export function SyncPausePanel() {
  const { t } = useLingui();
  const settings = useSyncSettings();
  const { pause, resume } = useSyncPauseActions();

  const loaded = settings.data !== undefined;
  const paused = settings.data?.syncPaused ?? false;
  // 開始の時刻が無い停止中は時刻を添えない（「（ から）」のような読めない文にしない）。
  const pausedAt = settings.data?.syncPausedAt;
  const pausedSince = pausedAt ? formatDateTime(pausedAt) : '';
  const pending = pause.isPending || resume.isPending;
  const failed = pause.isError ? pause : resume.isError ? resume : null;

  function toggle() {
    pause.reset();
    resume.reset();
    if (paused) resume.mutate();
    else pause.mutate();
  }

  return (
    <Panel aria-label={t`同期の状態`} heading={<Trans>同期の状態</Trans>} headingAs="h2">
      <div className="flex flex-col gap-2">
        {settings.isError && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {t`同期の状態を取得できませんでした。時間をおいて再度お試しください。`}
          </Alert>
        )}
        {failed && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {toMessages(
              failed.error,
              paused
                ? t`同期を再開できませんでした。時間をおいて再度お試しください。`
                : t`同期を一時停止できませんでした。時間をおいて再度お試しください。`,
            ).join(' ')}
          </Alert>
        )}
        <div className="flex flex-wrap items-center gap-2">
          {/* 状態は色だけでなく文字で示す（INDEX 決定 21）。
              🔴 **設定が読めていない間は「有効」と言わない**（AI レビュー #2384 🟡1）。実際は停止中の利用者に、読み込み中・
              取得の失敗中だけ「有効」と見せない —— 読み込み中は「確認中」、失敗は「状態を確認できません」と出す。 */}
          {settings.isError ? (
            <StatusBadge tone="danger">{t`同期: 状態を確認できません`}</StatusBadge>
          ) : !loaded ? (
            <StatusBadge tone="neutral">{t`同期: 確認中`}</StatusBadge>
          ) : paused ? (
            <StatusBadge tone="warning">
              {pausedSince ? t`同期: 一時停止中（${pausedSince} から）` : t`同期: 一時停止中`}
            </StatusBadge>
          ) : (
            <StatusBadge tone="success">{t`同期: 有効`}</StatusBadge>
          )}
          <Button
            variant={paused ? 'primary' : 'secondary'}
            disabled={!loaded || pending}
            onClick={toggle}
          >
            {paused ? <Trans>再開する</Trans> : <Trans>一時停止する</Trans>}
          </Button>
        </div>
        <Note>
          <Trans>
            一時停止すると、すべての端末の同期が止まります。トークンは残り、再開すれば同期が戻ります（再発行も入れ直しも要りません）。トークンの有効期限は、一時停止中も進みます。
          </Trans>
        </Note>
      </div>
    </Panel>
  );
}
