import { useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import {
  Alert,
  Button,
  EmptyState,
  Input,
  Label,
  Note,
  Panel,
  StatusBadge,
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
import type { SyncDeviceDto } from '@foundation/api/generated/bff.schemas';
import { ConfirmDialog } from '../../../components/ConfirmDialog';
import { useSyncDeviceActions, useSyncDevices } from '../api/useSyncDevices';
import { useIssuedToken } from '../hooks/useIssuedToken';
import { deviceView } from '../types/deviceState';
import type { DeviceState } from '../types/deviceState';
import { SyncTargetFoldersPanel } from './SyncTargetFoldersPanel';
import { SyncConflictsPanel } from './SyncConflictsPanel';

// SC-20, UC-11, FR-20: Obsidian 連携設定（05_screens: ルート /my/obsidian）。
//
// ■ 🔴 描かないもの（05_screens §SC-20「描いてはいけないもの」）
//     - 管理者による他利用者の同期設定の閲覧・変更
//     - 公開資料・組織文書を Obsidian へ同期する導線
//     - 端末登録時の管理者承認ステップ
//   いずれも「置いていない」ことを単体テストが**陽性対照と対で**固定する。
//
// ■ 平文のトークンは**発行・再発行の応答にしか載らない**。画面はそれをその場の状態として持ち、
//   次の操作を始めた時点で捨てる。保存もコピー履歴も残さない。
// ■ 自動更新（リフレッシュ）の導線を置かない。更新は**手動再発行だけ**である（ADR-0037 決定 15）。
//
// ■ 固定文言 3 段落のうち、**業務関連資料としての扱い（3 段落目）は同期対象範囲の区画の中にある**
//   （`SyncTargetFoldersPanel`）。計画が「フォルダ指定 UI のすぐ隣」と定めているためで、
//   区画が出来たいま画面上部から移した。**残る 2 段落（同期の範囲・削除の扱い）は上部のまま**である。
//
// ■ 🔴 **同期履歴は依然として描かない。** 引く口が無く、**N 件・保持期間が未確定**だからである
//   （05_screens §SC-20 主要素 6。planning#618 の裁定待ち）。件数と期間を画面が勝手に決めると、
//   裁定後に「画面に出ている履歴の長さ」と「実際に保持している長さ」が食い違う。

/** 確認ダイアログの種別。開いていないときは `null`。 */
type Confirmation = { kind: 'revoke'; device: SyncDeviceDto } | { kind: 'revokeAll' } | null;

export function ObsidianSettingsPage() {
  const { t } = useLingui();
  const devices = useSyncDevices();
  const actions = useSyncDeviceActions();
  const { issue, reissue, revoke, revokeAll } = actions;

  const [deviceName, setDeviceName] = useState('');
  // 平文トークンの保持先は hooks/ のローカル状態ただ 1 つに閉じる（ストアにも URL にも載せない）。
  const { issued, show: showIssuedToken, clear: clearIssuedToken } = useIssuedToken();
  const [confirming, setConfirming] = useState<Confirmation>(null);

  const rows = useMemo(() => devices.data ?? [], [devices.data]);
  // 「いま」は描画のたびに読み直さない（残り日数が描画のたびに揺れないようにする）。
  const now = useMemo(() => new Date(), []);

  const mutations = Object.values(actions);
  const failed = mutations.find((m) => m.isError);
  const pending = mutations.some((m) => m.isPending);

  // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す
  // （`lingui/no-expression-in-message`。プロパティ参照だとカタログのプレースホルダ名が揺れる）。
  const issuedDeviceName = issued?.deviceName ?? '';
  const issuedExpiresAt = formatDateTime(issued?.expiresAt);
  const revokeTargetName = confirming?.kind === 'revoke' ? confirming.device.deviceName : '';
  const deviceCount = rows.length;

  /** 新しい操作の前に、前回の失敗と**発行済みトークンの表示**を捨てる。 */
  function beginOperation() {
    clearIssuedToken();
    for (const mutation of mutations) mutation.reset();
  }

  function submitIssue() {
    beginOperation();
    issue.mutate(
      { data: { deviceName: deviceName.trim() } },
      {
        onSuccess: (response) => {
          setDeviceName('');
          if (response.status === 201) showIssuedToken(response.data);
        },
      },
    );
  }

  function submitReissue(device: SyncDeviceDto) {
    beginOperation();
    reissue.mutate(
      { id: device.id },
      {
        onSuccess: (response) => {
          if (response.status === 200) showIssuedToken(response.data);
        },
      },
    );
  }

  function confirmRevoke() {
    if (confirming?.kind !== 'revoke') return;
    const id = confirming.device.id;
    beginOperation();
    revoke.mutate({ id });
    setConfirming(null);
  }

  function confirmRevokeAll() {
    beginOperation();
    revokeAll.mutate();
    setConfirming(null);
  }

  /** 4 状態の表示（色 ＋ アイコン ＋ テキスト。色だけで意味を持たせない）。 */
  function stateBadge(state: DeviceState, daysLeft: number) {
    if (state === 'revoked') return <StatusBadge tone="neutral">{t`失効済み`}</StatusBadge>;
    if (state === 'expired')
      return <StatusBadge tone="danger">{t`期限切れ（同期は停止しています）`}</StatusBadge>;
    if (state === 'expiring')
      return <StatusBadge tone="warning">{t`期限切れ間近（残り ${daysLeft} 日）`}</StatusBadge>;
    return <StatusBadge tone="success">{t`有効（残り ${daysLeft} 日）`}</StatusBadge>;
  }

  return (
    <section className="flex flex-col gap-4">
      <h1 className="text-[17px] font-medium text-fg">
        <Trans>Obsidian 連携設定</Trans>
      </h1>

      {/*
        05_screens §SC-20「固定文言（確定）」の 3 段落。
        🔴 **削除の説明と業務関連資料の説明を同じ段落へまとめない**（別の性質の注意であり、
        まとめると読み飛ばされる）。2 段落目の「90 日を過ぎると……復元できなくなります」は必須である。
      */}
      <Alert tone="info" label={t`同期の範囲`}>
        <Trans>
          同期できるのは、あなたが作成した個人資料のみです。他の利用者の資料および組織文書は同期されません。公開範囲を変更しても同期は継続します。
        </Trans>
      </Alert>
      <Alert tone="info" label={t`削除の扱い`}>
        <Trans>
          Obsidian 側で削除した資料は、サーバ上では削除済みとして 90
          日間保管され、その間は復元できます。90
          日を過ぎると自動的に完全削除され、復元できなくなります。削除済み資料がある場合は週に一度お知らせし、完全削除の
          7 日前にも改めてお知らせします。
        </Trans>{' '}
        <Link to="/my/notes" search={{ tab: 'trash', q: '' }} className="underline">
          <Trans>削除済みの個人資料を確認する</Trans>
        </Link>
      </Alert>
      {/* 接続手順とトークンの発行（05_screens §SC-20 主要素 2）。管理者承認のステップは無い。 */}
      <Panel heading={<Trans>端末を接続する</Trans>} headingAs="h2">
        <div className="flex flex-col gap-2">
          <p className="text-sm">
            <Trans>
              端末名を入力してトークンを発行し、Obsidian
              プラグインの設定へ貼り付けてください。トークンの有効期限は 30
              日です。期限が切れたら、この画面から手動で再発行して入れ直してください（自動更新は行いません）。
            </Trans>
          </p>
          <div className="flex flex-wrap items-end gap-2">
            <div className="flex flex-col gap-1">
              <Label htmlFor="device-name">
                <Trans>端末名（任意）</Trans>
              </Label>
              <Input
                id="device-name"
                value={deviceName}
                onChange={(e) => setDeviceName(e.target.value)}
              />
            </div>
            <Button variant="primary" disabled={pending} onClick={submitIssue}>
              <Trans>トークンを発行する</Trans>
            </Button>
          </div>

          {issued && (
            // 🔴 平文が現れるのはここだけである。**再表示できない**旨を同じ枠の中に置く。
            <Alert tone="success" label={t`発行しました`} role="status">
              <span className="flex flex-col gap-1">
                <span>
                  <Trans>
                    このトークンを表示できるのは今回だけです。閉じると再表示できません（再発行のみ可能です）。
                  </Trans>
                </span>
                <code className="break-all rounded bg-surface-muted p-2 text-xs">
                  {issued.token}
                </code>
                <span>
                  <Trans>
                    端末: {issuedDeviceName} ／ 有効期限: {issuedExpiresAt}
                  </Trans>
                </span>
              </span>
            </Alert>
          )}
        </div>
      </Panel>

      {failed && (
        <Alert tone="danger" label={t`エラー`} role="alert">
          {toMessages(failed.error, t`操作に失敗しました。時間をおいて再度お試しください。`).join(
            ' ',
          )}
        </Alert>
      )}
      <section aria-label={t`接続端末`} className="flex flex-col gap-2">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold">
            <Trans>接続端末</Trans>
          </h2>
          {/* 端末紛失時の防御。個別失効とは別に必ず置く（05_screens §SC-20 主要素 2）。 */}
          <Button
            variant="danger"
            disabled={pending || rows.length === 0}
            onClick={() => setConfirming({ kind: 'revokeAll' })}
          >
            <Trans>すべての端末を失効する</Trans>
          </Button>
        </div>

        {/*
          05_screens §SC-20: 期限切れ後もプラグイン設定には古いトークンが残る。
          この文言が無いと、利用者は「トークンが入っているのに同期されない」原因を特定できない。
        */}
        <Alert tone="info" label={t`再発行したときの注意`}>
          <Trans>
            有効期限が切れたトークンは、サーバ側で無効になりますが、Obsidian
            プラグインの設定には残ったままです。再発行したトークンを、プラグインの設定へ入れ直してください。
          </Trans>
        </Alert>

        {/*
          待ち・失敗・空・本体の描き分けは `QueryState` に一本化した（判定順は失敗 → 待ち → 空 → 本体）。
          🔴 **0 件と失敗を混同しない** ——「端末が 1 台も無い」は正常な初期状態であり、
          「一覧が引けない」は同期の状況がまったく読めない状態である。失敗にだけ再試行を出す。
        */}
        <QueryState
          query={devices}
          isEmpty={(list: SyncDeviceDto[]) => list.length === 0}
          loadingLabel={t`接続端末の一覧を読み込み中…`}
          errorTitle={t`接続端末の一覧を取得できませんでした。`}
          empty={
            <EmptyState
              title={t`接続している端末はまだありません。`}
              description={t`上の入力欄に端末名を入れてトークンを発行し、Obsidian プラグインへ貼り付けてください。`}
            />
          }
        >
          {() => (
            <Table>
              <TableCaption>{t`接続端末の一覧`}</TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell scope="col">
                    <Trans>端末名</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>最終同期</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>有効期限</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>状態</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>操作</Trans>
                  </TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map((device) => {
                  const view = deviceView(device, now);
                  return (
                    <TableRow key={device.id}>
                      <TableCell>{device.deviceName}</TableCell>
                      <TableCell>{formatDateTime(device.lastSyncAt)}</TableCell>
                      <TableCell>{formatDateTime(device.expiresAt)}</TableCell>
                      <TableCell>{stateBadge(view.state, view.daysLeft)}</TableCell>
                      <TableCell>
                        <div className="flex gap-2">
                          {/* 期限切れの行には再発行を同じ行に置く（05_screens §SC-20）。 */}
                          {view.state !== 'revoked' && (
                            <Button
                              size="sm"
                              variant="secondary"
                              disabled={pending}
                              onClick={() => submitReissue(device)}
                            >
                              <Trans>再発行する</Trans>
                            </Button>
                          )}
                          {/*
                          🔴 個別失効は端末紛失時の唯一の防御線であり、**失効済み以外の全行に置く**。
                          期限切れの端末も、紛失していれば利用者は失効させたい。
                        */}
                          {view.state !== 'revoked' && (
                            <Button
                              size="sm"
                              variant="danger"
                              disabled={pending}
                              onClick={() => setConfirming({ kind: 'revoke', device })}
                            >
                              <Trans>この端末を失効する</Trans>
                            </Button>
                          )}
                        </div>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
        </QueryState>
      </section>

      {/* 主要素 3: 同期対象範囲。**業務関連資料の固定文言はこの区画の中にある**（冒頭の注記）。 */}
      <SyncTargetFoldersPanel />

      {/* 主要素 5: 競合の一覧・2 ペイン差分・3 択（自動解決は置かない）。 */}
      <SyncConflictsPanel />

      {/* 露出設定は資料単位で個人資料管理画面が持つ（作業仕様書 §計画との差異 を参照）。 */}
      <Note>
        <Trans>
          横断検索・ナレッジグラフ・AI
          の入力に含めるかどうかは、資料ごとに個人資料の一覧から設定します。既定はいずれもオフです。
        </Trans>{' '}
        <Link to="/my/notes" search={{ tab: 'active', q: '' }} className="underline">
          <Trans>個人資料の一覧へ</Trans>
        </Link>
      </Note>

      {confirming?.kind === 'revoke' && (
        <ConfirmDialog
          title={t`この端末を失効しますか？`}
          confirmLabel={t`失効する`}
          cancelLabel={t`やめる`}
          destructive
          pending={pending}
          onConfirm={confirmRevoke}
          onCancel={() => setConfirming(null)}
        >
          <p>
            <Trans>
              「{revokeTargetName}
              」のトークンを無効にします。この端末からの同期はすぐに停止します。再び同期するには、トークンを再発行してプラグインへ入れ直してください。
            </Trans>
          </p>
        </ConfirmDialog>
      )}

      {confirming?.kind === 'revokeAll' && (
        <ConfirmDialog
          title={t`すべての端末を失効しますか？`}
          confirmLabel={t`すべて失効する`}
          cancelLabel={t`やめる`}
          destructive
          pending={pending}
          onConfirm={confirmRevokeAll}
          onCancel={() => setConfirming(null)}
        >
          <p>
            <Trans>
              登録されているすべての端末のトークンを無効にします。この操作は元に戻せません。すべての端末で同期が停止し、再開するには端末ごとにトークンを再発行して入れ直す必要があります。
            </Trans>
          </p>
          <p>
            <Trans>対象: {deviceCount} 台</Trans>
          </p>
        </ConfirmDialog>
      )}
    </section>
  );
}
