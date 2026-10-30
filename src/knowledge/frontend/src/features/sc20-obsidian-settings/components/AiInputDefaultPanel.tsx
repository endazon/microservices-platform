import { Trans, useLingui } from '@lingui/react/macro';
import { Alert, Panel } from '@platform/ui';
import { toMessages } from '@foundation/utils/apiErrors';
import { useSyncSettings, useSyncSettingsActions } from '../api/useSyncSettings';

// FR-19, SC-20 主要素 8, ADR-0143 決定 1・2・4 (#2120): AI 入力の**利用者単位の既定**の区画。
//
// ■ 既定は**新しく作る資料（SC-19 の作成・同期の新規 push）の AI 入力の初期値**である。作成後は
//   資料単位の値だけが効き、既定を変えても既存の資料は変わらない（決定 1・2）。
// ■ 🔴 **横断検索・ナレッジグラフの既定は置かない**（決定 4）。トグルは AI 入力の 1 つだけである。
// ■ 🔴 **既定を ON にする操作のすぐ近くで帰結を伝える**（決定 4）—— 同期対象フォルダへまとめて入れた
//   メモも、作成の時点ですべて AI の入力に含まれる。文言は計画の SC-20 主要素 8 のとおり。
// ■ 契約の置き換えは**全量**なので、フォルダはいま見えている値のまま送る（既定を変えてフォルダを消さない）。
//   フォルダの区画は `aiInputDefault` を送らない（省略は変えない）。

export function AiInputDefaultPanel() {
  const { t } = useLingui();
  const settings = useSyncSettings();
  const { update } = useSyncSettingsActions();

  const loaded = settings.data !== undefined;
  const checked = settings.data?.aiInputDefault ?? false;

  function change(next: boolean) {
    if (!settings.data) return;
    update.reset();
    update.mutate({
      data: {
        targetFolders: settings.data.targetFolders.map((f) => f.path),
        aiInputDefault: next,
      },
    });
  }

  return (
    <Panel aria-label={t`AI 入力の既定`} heading={<Trans>AI 入力の既定</Trans>} headingAs="h2">
      <div className="flex flex-col gap-2">
        {update.isError && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {toMessages(
              update.error,
              t`AI 入力の既定を更新できませんでした。時間をおいて再度お試しください。`,
            ).join(' ')}
          </Alert>
        )}
        <label className="flex items-center gap-1">
          <input
            type="checkbox"
            checked={checked}
            disabled={!loaded || update.isPending}
            onChange={(e) => change(e.target.checked)}
          />
          <Trans>新しく作る資料を AI の入力に含める</Trans>
        </label>
        <Alert tone="warning" label={t`既定の効き方`}>
          <Trans>
            これから作る資料と、同期対象フォルダに新しく入れたメモが AI
            の入力に含まれます。既存の資料は変わりません。
          </Trans>
        </Alert>
      </div>
    </Panel>
  );
}
