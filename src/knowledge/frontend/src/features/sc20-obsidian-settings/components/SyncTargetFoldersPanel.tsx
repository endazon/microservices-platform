import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  EmptyState,
  Input,
  Label,
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
import type { SyncSettingsDto } from '@foundation/api/generated/bff.schemas';
import { ConfirmDialog } from '../../../components/ConfirmDialog';
import { useSyncSettings, useSyncSettingsActions } from '../api/useSyncSettings';
import { canAddFolder, normalizeFolderPath } from '../types/syncFolders';

// SC-20 主要素 3, UC-11, FR-20, ADR-0037 決定 3・4: 同期対象範囲（フォルダ指定）の区画。
//
// ■ 🔴 **「対象フォルダから外す」は「削除する」ではない。** 外しても資料はサーバに残り、
//   同期が止まるだけである（決定 4）。両者を UI 上で読み分けられないと、利用者は
//   **資料が消えると思って外せない**か、**消える覚悟がないまま外す**かのどちらかになる。
//   よって操作の名前を「外す」とし、確認ダイアログの本文で**削除ではない**と明言する。
//
// ■ 🔴 **業務関連資料としての扱いの固定文言はこの区画の中に置く**（05_screens §SC-20 主要素 3）。
//   計画は「フォルダ指定 UI のすぐ隣」と定めている —— 私的なメモを対象フォルダへ入れる、
//   まさにその操作の手元に無いと、注意として働かない。
//   （区画が無かった間は画面上部に置いていた。画面仕様書 §未決事項 1 の解消。）
//
// ■ フォルダ未設定は「全資料が対象」という**既定の状態**であり、異常ではない（決定 3）。
//   空状態の文言でそれを明言する（「まだ設定していない＝同期されない」と読ませない）。
//
// ■ 正規化はサーバが持つ（`types/syncFolders.ts` の注記）。ここでは押せないボタンを
//   押させないためだけに同じ判定を使う。

export function SyncTargetFoldersPanel() {
  const { t } = useLingui();
  const settings = useSyncSettings();
  const { update } = useSyncSettingsActions();

  const [draftPath, setDraftPath] = useState('');
  /** 「外す」確認の対象パス。開いていないときは `null`。 */
  const [removing, setRemoving] = useState<string | null>(null);

  const folders = settings.data?.targetFolders ?? [];
  const paths = folders.map((f) => f.path);
  const pending = update.isPending;

  /** 置き換えは**全量**である（契約に差分の口が無い）。 */
  function replaceFolders(next: string[], onDone?: () => void) {
    update.reset();
    update.mutate({ data: { targetFolders: next } }, { onSuccess: onDone });
  }

  function submitAdd() {
    replaceFolders([...paths, normalizeFolderPath(draftPath)], () => setDraftPath(''));
  }

  function confirmRemove() {
    if (removing === null) return;
    replaceFolders(paths.filter((p) => p !== removing));
    setRemoving(null);
  }

  return (
    <Panel aria-label={t`同期対象範囲`} heading={<Trans>同期対象範囲</Trans>} headingAs="h2">
      <div className="flex flex-col gap-2">
        <p className="text-sm">
          <Trans>
            同期するフォルダを Obsidian Vault 内の相対パスで指定します。フォルダを 1
            つも指定していない場合は、あなたの個人資料がすべて同期の対象です。
          </Trans>
        </p>

        {update.isError && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {toMessages(
              update.error,
              t`同期対象フォルダを更新できませんでした。時間をおいて再度お試しください。`,
            ).join(' ')}
          </Alert>
        )}

        <QueryState
          query={settings}
          isEmpty={(data: SyncSettingsDto) => data.targetFolders.length === 0}
          loadingLabel={t`同期対象フォルダを読み込み中…`}
          errorTitle={t`同期対象フォルダを取得できませんでした。`}
          empty={
            <EmptyState
              title={t`同期対象フォルダを指定していません。`}
              description={t`いまは、あなたの個人資料がすべて同期の対象です。特定のフォルダだけを同期したい場合は、下の入力欄からパスを追加してください。`}
            />
          }
        >
          {() => (
            <Table>
              <TableCaption>{t`同期対象フォルダの一覧`}</TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell scope="col">
                    <Trans>フォルダのパス</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>配下の資料数</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>最終同期</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>操作</Trans>
                  </TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {folders.map((folder) => (
                  <TableRow key={folder.path}>
                    <TableCell>{folder.path}</TableCell>
                    <TableCell>{folder.noteCount}</TableCell>
                    <TableCell>{formatDateTime(folder.lastSyncAt)}</TableCell>
                    <TableCell>
                      {/*
                        🔴 ラベルは「外す」。**`danger` にしない** —— 資料が消えないのだから、
                        完全削除や失効と同じ強さで見せるのは誤りである（強い色を濫用すると、
                        本当に取り返しのつかない操作の色が効かなくなる）。
                      */}
                      <Button
                        size="sm"
                        variant="secondary"
                        disabled={pending}
                        onClick={() => setRemoving(folder.path)}
                      >
                        <Trans>対象フォルダから外す</Trans>
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </QueryState>

        {/* 追加。**正規化はサーバ任せ**で、ここは空・重複のときに押させないだけである。 */}
        <div className="flex flex-wrap items-end gap-2">
          <div className="flex flex-col gap-1">
            <Label htmlFor="sync-folder-path">
              <Trans>追加するフォルダのパス</Trans>
            </Label>
            <Input
              id="sync-folder-path"
              value={draftPath}
              onChange={(e) => setDraftPath(e.target.value)}
            />
          </div>
          <Button
            variant="primary"
            disabled={pending || !canAddFolder(draftPath, paths)}
            onClick={submitAdd}
          >
            <Trans>フォルダを追加する</Trans>
          </Button>
        </div>

        {/*
          05_screens §SC-20「固定文言（確定）」の 3 段落目。**フォルダ指定 UI のすぐ隣に置く**
          （上の注記）。他の 2 段落は画面上部の枠のままである。
        */}
        <Alert tone="warning" label={t`取り扱い`}>
          <Trans>
            同期した資料は業務関連資料として扱われます。退職時には、退職日から 30
            日間、管理者が閲覧することがあります。同期対象フォルダに入れた私的なメモも、ナレッジベースに入った時点で同じ扱いになります。
          </Trans>
        </Alert>
      </div>

      {removing !== null && (
        <ConfirmDialog
          title={t`このフォルダを同期対象から外しますか？`}
          confirmLabel={t`対象から外す`}
          cancelLabel={t`やめる`}
          pending={pending}
          onConfirm={confirmRemove}
          onCancel={() => setRemoving(null)}
        >
          {/* 🔴 「削除ではない」を最初の文で言い切る（ADR-0037 決定 4）。 */}
          <p>
            <Trans>
              これは削除ではありません。フォルダ配下の資料はサーバに残り、Obsidian
              との同期が止まるだけです。資料を消したい場合は、個人資料の一覧から削除してください。
            </Trans>
          </p>
          <p>
            <Trans>対象: {removing}</Trans>
          </p>
        </ConfirmDialog>
      )}
    </Panel>
  );
}
