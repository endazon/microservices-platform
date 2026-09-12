import { Trans, useLingui } from '@lingui/react/macro';
import { i18n } from '@lingui/core';
import type { MessageDescriptor } from '@lingui/core';
import {
  EmptyState,
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
import type { SyncHistoryEntryDto } from '@foundation/api/generated/bff.schemas';
import { useSyncHistory } from '../api/useSyncHistory';
import { breakdownOf, directionLabel, failureReasonText, isSuccess } from '../types/syncHistory';
import type { SyncCountPart } from '../types/syncHistory';

// SC-20 主要素 6, UC-11, FR-20, ADR-0037 決定 9 / ADR-0099 / IADR-0446: 同期履歴の区画。
//
// ■ 🔴 **タイトルとパスの列を持たない。** ADR-0099 決定 5 が「資料のタイトルも Vault のパスも
//   含めない」と定め、契約（`SyncHistoryEntryDto`）にもその項目が無い。理由は 2 つある ——
//   ①同じ記録を第三者へ開くかが未確定であり（決定 2）、開いた瞬間に題名が第三者へ渡る、
//   ②**完全削除（ADR-0096）が空文になる** —— 資料を消しても題名だけが 3 年間履歴に残る。
//   **「置いていない」ことは単体テストが陽性対照（端末の列は在る）と対で固定する。**
//
// ■ 🔴 **上の「同期の競合」区画とは別の記録である。** 競合の一覧（主要素 5）が指すのは
//   **いま存在する資料**でありタイトルを出してよいが、履歴が指すのは**過去の事象**であり
//   資料は既に無いことがある（ADR-0099 決定 5 の注記）。**2 つの区画を統合しない。**
//
// ■ 失敗理由の文言は `types/syncHistory.ts` が持つ（契約はコードだけを運ぶ）。
//   **コードをそのまま出さない** —— 「version_conflict」と書かれても利用者は次の手を選べない。
//
// ■ 結果は**色だけに頼らない**（INDEX 決定 21）。`StatusBadge` が tone ごとの固定アイコンを付け、
//   文言（「成功」「失敗」）も併記する。失敗を `danger` ではなく `warning` にするのは、
//   **同期の失敗は利用者の操作で解消できる**状態であり、取り返しのつかない事故ではないためである。
//
// ■ 🔴 **0 件の読み分け**（ADR-0099 §残るもの の帰結。#1447）。履歴は**本機能の配備後の同期から**
//   記録される。配備前に同期していた利用者には、0 件が「記録されていない」のか「同期していない」のか
//   区別できない —— **同じ 0 件に 2 つの意味がある。** 端末のいずれかに最終同期があれば
//   「配備前の同期は表示されない」と告げ、無ければ従前どおり「同期すると並ぶ」と案内する。
//   **判断の材料（`hasPriorSync`）は本区画では引かない** —— 端末一覧は親（`ObsidianSettingsPage`）が
//   既に引いており、同じ一覧を 2 度引くと「どちらの応答で描いたか」が読めなくなる。

/** `MessageDescriptor` と生値の両方を受ける（未知の方向は翻訳せず生値で出る）。 */
function labelOf(label: MessageDescriptor | string): string {
  return typeof label === 'string' ? label : i18n._(label);
}

/** 内訳セル。**0 の項目は出さず、すべて 0 なら「—」**（`breakdownOf` の注記）。 */
function BreakdownCell({ entry }: { entry: SyncHistoryEntryDto }) {
  const { t } = useLingui();
  const parts = breakdownOf(entry);
  if (parts.length === 0) return <span className="text-xs text-fg-muted">{t`—`}</span>;
  // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す
  // （`lingui/no-expression-in-message`。プロパティ参照だとカタログのプレースホルダ名が揺れる）。
  const textOf = (part: SyncCountPart): string => {
    const count = part.count;
    if (part.kind === 'added') return t`追加 ${count}`;
    if (part.kind === 'updated') return t`更新 ${count}`;
    if (part.kind === 'deleted') return t`削除 ${count}`;
    return t`競合 ${count}`;
  };
  return (
    <span className="flex flex-wrap gap-x-2 text-xs whitespace-nowrap">
      {parts.map((part) => (
        <span key={part.kind}>{textOf(part)}</span>
      ))}
    </span>
  );
}

export interface SyncHistoryPanelProps {
  /**
   * 接続端末のいずれかに最終同期があるか。
   *
   * 🔴 **「履歴が空である」ことの意味を決める唯一の手掛かりである。** 真なら過去に同期しており、
   * それでも履歴が空なのは**配備前の同期が記録に無い**ためである（ADR-0099）。
   */
  hasPriorSync: boolean;
}

export function SyncHistoryPanel({ hasPriorSync }: SyncHistoryPanelProps) {
  const { t } = useLingui();
  const history = useSyncHistory();

  return (
    <Panel aria-label={t`同期履歴`} heading={<Trans>同期履歴</Trans>} headingAs="h2">
      <div className="flex flex-col gap-2">
        <p className="text-sm">
          <Trans>
            端末ごとの同期の結果です。失敗した同期も記録されます。あなた自身の記録だけが表示されます。
          </Trans>
        </p>

        <QueryState
          query={history}
          isEmpty={(list: SyncHistoryEntryDto[]) => list.length === 0}
          loadingLabel={t`同期履歴を読み込み中…`}
          errorTitle={t`同期履歴を取得できませんでした。`}
          empty={
            <EmptyState
              title={t`同期の記録はまだありません。`}
              // 🔴 **題は共通・案内だけを分ける。** 0 件であること自体は同じ事実であり、
              // 変わるのは「なぜ 0 件なのか」だけである。
              description={
                hasPriorSync
                  ? t`同期履歴の記録は本機能の配備後の同期から残ります。配備前の同期は表示されません。`
                  : t`Obsidian プラグインから同期すると、ここに結果が並びます。`
              }
            />
          }
        >
          {(rows) => (
            <Table>
              <TableCaption>{t`直近の同期履歴`}</TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell scope="col">
                    <Trans>実行日時</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>端末</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>方向</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>内訳</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>結果</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell scope="col">
                    <Trans>失敗理由</Trans>
                  </TableHeaderCell>
                  {/* 🔴 タイトル・パスの列は置かない（冒頭の注記）。 */}
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map((entry) => {
                  const succeeded = isSuccess(entry.outcome);
                  return (
                    <TableRow key={entry.id}>
                      <TableCell>{formatDateTime(entry.occurredAt)}</TableCell>
                      <TableCell>{entry.deviceName}</TableCell>
                      <TableCell>{labelOf(directionLabel(entry.direction))}</TableCell>
                      <TableCell>
                        <BreakdownCell entry={entry} />
                      </TableCell>
                      <TableCell>
                        <StatusBadge tone={succeeded ? 'success' : 'warning'}>
                          {succeeded ? t`成功` : t`失敗`}
                        </StatusBadge>
                      </TableCell>
                      <TableCell>
                        {/* 成功の行には理由が無い。**空欄にせず「—」を出す**（取得漏れと区別する）。 */}
                        {entry.failureReason
                          ? i18n._(failureReasonText(entry.failureReason))
                          : t`—`}
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
        </QueryState>

        {/*
          ADR-0099 決定 3・4・5: **表示の件数と保持の期間は別**である。
          利用者が「50 件より前は消えた」と誤解しないよう、同じ枠で両方を言う。
          タイトルを残さないことも明示する（出ていないだけでなく、記録していない）。
        */}
        <Note>
          <Trans>
            直近 50 件を表示します。記録は 3 年間保持されます。資料の名前は履歴に残しません。
          </Trans>
        </Note>
      </div>
    </Panel>
  );
}
