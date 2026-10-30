import { createContext, useContext, useState } from 'react';
import type { ReactNode } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Alert, Button, Input, Label, Note, Select } from '@platform/ui';
import { toMessages } from '@foundation/utils/apiErrors';
import {
  usePrivateNoteQuota,
  usePrivateNoteQuotaLookup,
  useSetPrivateNoteQuota,
} from '../api/useUserAccounts';
import type { PrivateNoteOwnerUsageDto } from '@foundation/api/generated/bff.schemas';
import { formatQuotaBytes, quotaInputToBytes } from '../types/privateNoteQuota';
import type { QuotaUnit } from '../types/privateNoteQuota';

// FR-19, NFR-27, SC-17 (#2330): 個人資料の保存容量（使用量・上限）の欄と、上限の変更。
//
// ■ 🔴 **見せるのは使用量と上限の 2 つの数だけ**。題名・件数・中身・同期設定は出さない
//   （SC-19 の「管理者が他の利用者の個人資料を閲覧する画面を描かない」。応答にも無い）。
// ■ 上限は 0〜1 TB（既定 1 GB）。**0 を許す** —— 0 は「新しい資料を作れない状態」で、
//   既存の資料の閲覧と更新はできる（FR-19 の容量 100% 到達と同じ扱い）。範囲外は押させない（後段も 400）。

const UNITS: readonly QuotaUnit[] = ['MB', 'GB', 'TB'];

// #2348: 一覧の行は、一覧の利用者（絞り込み前の全行）をまとめて引いた結果から描く（行ごとに照会しない＝N+1 を解く）。
const QuotaLookupContext = createContext<ReadonlyMap<string, PrivateNoteOwnerUsageDto>>(new Map());

/** 一覧の利用者名でまとめて 1 本引き（1,000 人ずつ）、その下の行の欄へ配る。 */
export function PrivateNoteQuotaLookupProvider({
  usernames,
  children,
}: {
  usernames: readonly string[];
  children: ReactNode;
}) {
  const byOwner = usePrivateNoteQuotaLookup(usernames);
  return <QuotaLookupContext.Provider value={byOwner}>{children}</QuotaLookupContext.Provider>;
}

/** 一覧の行: 「使用量 / 上限」。読めない間・読めない行は「—」（推測の値を出さない）。 */
export function PrivateNoteQuotaCell({ username }: { username: string }) {
  const quota = useContext(QuotaLookupContext).get(username);
  if (!quota) return <span className="text-fg-muted">—</span>;
  return (
    <span className="text-xs">
      {`${formatQuotaBytes(quota.usedBytes)} / ${formatQuotaBytes(quota.limitBytes)}`}
    </span>
  );
}

/** いまの上限を、割り切れる最大の単位で入力欄の初期値へ写す（割り切れなければ GB の小数）。 */
function initialInput(limitBytes: number): { amount: string; unit: QuotaUnit } {
  for (const unit of ['TB', 'GB', 'MB'] as const) {
    const per = quotaInputToBytes('1', unit) ?? 1;
    if (limitBytes > 0 && limitBytes % per === 0) return { amount: String(limitBytes / per), unit };
  }
  const gb = quotaInputToBytes('1', 'GB') ?? 1;
  return { amount: String(Number((limitBytes / gb).toFixed(3))), unit: 'GB' };
}

/** 編集の区画: 上限の変更（利用者ごと）。いまの上限を読めてから描く。 */
export function PrivateNoteQuotaEditor({ username }: { username: string }) {
  const quota = usePrivateNoteQuota(username);
  if (!quota.data) return null;
  // 利用者・いまの上限が変わったら入力を作り直す（下書きを別の人へ持ち越さない）。
  return (
    <PrivateNoteQuotaForm
      key={`${username}:${quota.data.limitBytes}`}
      username={username}
      limitBytes={quota.data.limitBytes}
    />
  );
}

function PrivateNoteQuotaForm({ username, limitBytes }: { username: string; limitBytes: number }) {
  const { t } = useLingui();
  const setQuota = useSetPrivateNoteQuota(username);
  const initial = initialInput(limitBytes);
  const [amount, setAmount] = useState(initial.amount);
  const [unit, setUnit] = useState<QuotaUnit>(initial.unit);
  const bytes = quotaInputToBytes(amount, unit);

  return (
    <div className="mt-3" data-testid="private-note-quota">
      <div className="flex flex-wrap items-end gap-2">
        <div>
          <Label htmlFor="private-note-quota-amount">{t`個人資料の保存容量の上限`}</Label>
          <Input
            id="private-note-quota-amount"
            inputSize="sm"
            inputMode="decimal"
            invalid={bytes === null}
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
          />
        </div>
        <div>
          <Label htmlFor="private-note-quota-unit">{t`単位`}</Label>
          <Select
            id="private-note-quota-unit"
            selectSize="sm"
            value={unit}
            onChange={(e) => setUnit(e.target.value as QuotaUnit)}
          >
            {UNITS.map((u) => (
              <option key={u} value={u}>
                {u}
              </option>
            ))}
          </Select>
        </div>
        <Button
          disabled={bytes === null || setQuota.isPending}
          onClick={() => {
            if (bytes !== null) setQuota.mutate({ ownerId: username, data: { limitBytes: bytes } });
          }}
        >
          <Trans>上限を保存</Trans>
        </Button>
      </div>
      {bytes === null && (
        <p className="mt-1 text-xs text-danger">
          <Trans>0〜1 TB の範囲で指定してください。</Trans>
        </p>
      )}
      {bytes === 0 && (
        <Note>
          <Trans>
            上限が 0 のあいだは、新しい資料を作れません（既存の資料の閲覧と更新はできます）。
          </Trans>
        </Note>
      )}
      {setQuota.isError && (
        <Alert tone="danger" role="alert" label={t`上限を保存できませんでした`} className="mt-2">
          {toMessages(setQuota.error, '').join(' / ')}
        </Alert>
      )}
    </div>
  );
}
